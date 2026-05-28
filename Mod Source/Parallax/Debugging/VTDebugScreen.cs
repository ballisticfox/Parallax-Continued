using System.Collections.Generic;
using System.Text;
using KSP.UI.Screens.DebugToolbar;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Parallax.Debugging
{
    // Registers a "Parallax VT" entry in the Alt+F12 debug menu. Built with plain Unity UI
    // (KSPTextureLoader's DebugUIManager is internal so we can't reuse its styled prefabs).
    [KSPAddon(KSPAddon.Startup.MainMenu, once: true)]
    public class VTDebugRegistrar : MonoBehaviour
    {
        const string ScreenName = "ParallaxVT";
        const string ScreenText = "Parallax VT";

        void Start()
        {
            var spawner = DebugScreenSpawner.Instance;
            if (spawner == null || spawner.debugScreens == null)
            {
                ParallaxDebug.Log("VTDebugRegistrar: DebugScreenSpawner not ready, skipping.");
                return;
            }

            // Don't re-add if KSP was reloaded with the screen prefab still around.
            foreach (var existing in spawner.debugScreens.screens)
                if (existing.name == ScreenName) return;

            var root = new GameObject("Parallax_VTDebugScreen", typeof(RectTransform));
            root.SetActive(false);
            DontDestroyOnLoad(root);

            var rect = root.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var vlg = root.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 4f;
            vlg.padding = new RectOffset(8, 8, 8, 8);

            var content = root.AddComponent<VTDebugScreenContent>();
            content.BuildUI();

            spawner.debugScreens.screens.Add(new VTScreenWrapper
            {
                parentName = null,
                name       = ScreenName,
                text       = ScreenText,
                screen     = rect,
            });
        }

        class VTScreenWrapper : AddDebugScreens.ScreenWrapper
        {
            public override string ToString() => name;
        }
    }

    public class VTDebugScreenContent : MonoBehaviour
    {
        const string DebugKeyword       = "PARALLAX_VT_DEBUG";
        const string DebugModeUniform   = "_VTDebugMode"; // 0 = resident, 1 = desired

        // [SerializeField] so Unity preserves these references when KSP clones the screen prefab.
        // Without it, the cloned component sees null and Update() short-circuits, leaving the
        // initial BuildUI text frozen on screen.
        [SerializeField] TextMeshProUGUI statsText;
        [SerializeField] Toggle          debugColorToggle;
        [SerializeField] Toggle          desiredLevelToggle;

        readonly StringBuilder sb = new StringBuilder(1024);

        // Runs on the cloned instance (the prefab stays inactive and never Awakes).
        // Re-wires the toggle listeners because UnityEvent runtime listeners don't survive cloning.
        void Awake()
        {
            if (debugColorToggle != null)
            {
                debugColorToggle.onValueChanged.RemoveListener(OnDebugColorToggled);
                debugColorToggle.isOn = Shader.IsKeywordEnabled(DebugKeyword);
                debugColorToggle.onValueChanged.AddListener(OnDebugColorToggled);
            }
            if (desiredLevelToggle != null)
            {
                desiredLevelToggle.onValueChanged.RemoveListener(OnDesiredLevelToggled);
                desiredLevelToggle.isOn = Shader.GetGlobalFloat(DebugModeUniform) > 0.5f;
                desiredLevelToggle.onValueChanged.AddListener(OnDesiredLevelToggled);
            }
        }

        public void BuildUI()
        {
            var t = transform;

            AddLabel(t, "Parallax Virtual Texture", bold: true, size: 16f);
            AddSpacer(t, 6f);

            debugColorToggle   = BuildToggle(t, "Debug colour-by-VT-level",            Shader.IsKeywordEnabled(DebugKeyword));
            desiredLevelToggle = BuildToggle(t, "Show desired level (instead of resident)", Shader.GetGlobalFloat(DebugModeUniform) > 0.5f);
            debugColorToggle.onValueChanged.AddListener(OnDebugColorToggled);
            desiredLevelToggle.onValueChanged.AddListener(OnDesiredLevelToggled);

            AddSpacer(t, 10f);
            AddLabel(t, "Color legend: red=L0 orange=L1 yellow=L2 lime=L3 green=L4 cyan=L5 blue=L6", bold: false, size: 11f);

            AddSpacer(t, 10f);
            AddLabel(t, "Streaming Stats", bold: true, size: 14f);
            AddSpacer(t, 2f);

            var statsGo = new GameObject("Stats", typeof(RectTransform));
            statsGo.transform.SetParent(t, false);
            statsText = statsGo.AddComponent<TextMeshProUGUI>();
            statsText.fontSize = 12f;
            statsText.text = "(no VT bodies registered)";
            statsText.enableWordWrapping = false;
            statsText.overflowMode = TextOverflowModes.Overflow;
            statsText.alignment = TextAlignmentOptions.TopLeft;
            var statsLayout = statsGo.AddComponent<LayoutElement>();
            statsLayout.flexibleHeight = 1f;
        }

        static Toggle BuildToggle(Transform parent, string label, bool initialState)
        {
            var row = new GameObject("ToggleRow", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8f;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = false;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            row.AddComponent<LayoutElement>().minHeight = 24f;

            // Checkbox background
            var bgGo = new GameObject("Checkbox", typeof(RectTransform));
            bgGo.transform.SetParent(row.transform, false);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.color = new Color(0.25f, 0.25f, 0.25f, 1f);
            var bgRect = bgGo.GetComponent<RectTransform>();
            bgRect.sizeDelta = new Vector2(20f, 20f);
            var bgLayout = bgGo.AddComponent<LayoutElement>();
            bgLayout.minWidth = 20f;
            bgLayout.preferredWidth = 20f;
            bgLayout.minHeight = 20f;
            bgLayout.preferredHeight = 20f;

            // Checkmark
            var checkGo = new GameObject("Check", typeof(RectTransform));
            checkGo.transform.SetParent(bgGo.transform, false);
            var checkImg = checkGo.AddComponent<Image>();
            checkImg.color = new Color(0.95f, 0.85f, 0.2f, 1f);
            var checkRect = checkGo.GetComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0.15f, 0.15f);
            checkRect.anchorMax = new Vector2(0.85f, 0.85f);
            checkRect.offsetMin = Vector2.zero;
            checkRect.offsetMax = Vector2.zero;

            var toggle = bgGo.AddComponent<Toggle>();
            toggle.targetGraphic = bgImg;
            toggle.graphic = checkImg;
            toggle.isOn = initialState;

            AddLabel(row.transform, label, bold: false, size: 13f);
            return toggle;
        }

        static void OnDesiredLevelToggled(bool isOn)
        {
            Shader.SetGlobalFloat(DebugModeUniform, isOn ? 1f : 0f);
        }

        void Update()
        {
            if (statsText == null) return;

            var bodies = TileStreamingManager.GetAllBodyDebugInfo();
            if (bodies.Count == 0)
            {
                statsText.text = "(no VT bodies registered)";
                return;
            }

            sb.Length = 0;
            for (int b = 0; b < bodies.Count; b++)
            {
                var info = bodies[b];
                sb.Append(info.sphereName).Append('\n');

                AppendCacheLine(sb, "Color ",  info.colorSlots,  info.colorTotal,
                                 info.colorQueue,  info.colorFlight,  info.colorLevelCounts);
                AppendCacheLine(sb, "Height", info.heightSlots, info.heightTotal,
                                 info.heightQueue, info.heightFlight, info.heightLevelCounts);

                sb.Append("  req:").Append(info.tilesRequested)
                  .Append("  loaded:").Append(info.tilesLoaded).Append('\n');

                if (b < bodies.Count - 1) sb.Append('\n');
            }

            statsText.text = sb.ToString();
        }

        static void AppendCacheLine(StringBuilder sb, string label, int slots, int total,
                                     int queue, int flight, int[] levelCounts)
        {
            sb.Append("  ").Append(label)
              .Append("  slots:").Append(slots).Append('/').Append(total)
              .Append("  Q:").Append(queue).Append("  F:").Append(flight);

            if (levelCounts != null && levelCounts.Length > 0)
            {
                sb.Append("  [");
                for (int i = 0; i < levelCounts.Length; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append('L').Append(i).Append(':').Append(levelCounts[i]);
                }
                sb.Append(']');
            }
            sb.Append('\n');
        }

        static void OnDebugColorToggled(bool isOn)
        {
            if (isOn) Shader.EnableKeyword(DebugKeyword);
            else      Shader.DisableKeyword(DebugKeyword);
        }

        static void AddLabel(Transform parent, string text, bool bold, float size)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = size + 4f;
            le.preferredHeight = size + 4f;
        }

        static void AddSpacer(Transform parent, float height)
        {
            var go = new GameObject("Spacer", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
        }
    }
}
