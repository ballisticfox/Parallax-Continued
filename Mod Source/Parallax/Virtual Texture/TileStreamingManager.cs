using System;
using System.Collections.Generic;
using UnityEngine;
using KSPTextureLoader;

namespace Parallax
{
    /// <summary>
    /// CPU-driven virtual texture streaming manager (Stage 2).
    ///
    /// Each frame RuntimeOperations calls Update(frameCount).  For every body that opted into VT
    /// streaming the manager:
    ///   1. Scans visible PQS quads to determine which tiles are needed.
    ///   2. Marks already-resident tiles as used (refreshes LRU).
    ///   3. Queues loads for tiles that are not resident and not already loading.
    ///   4. Starts new async loads (capped at MaxConcurrentLoads).
    ///   5. Ticks in-flight handles; uploads completed tiles (capped at MaxUploadsPerFrame).
    ///   6. Flushes page table once per frame.
    ///
    /// Coarse levels (0..CoarseMaxLevel) are always resident (pinned by TileCache.BootstrapCoarseLevels)
    /// so the shader always has a fallback.  The streaming manager only requests fine levels
    /// (CoarseMaxLevel+1..maxLevel) for visible quads.
    ///
    /// Tile coordinates (tx, ty) are always in CORRECTED face-UV space so they match the shader's
    /// page-table lookup.  See GetCorrectedTileCoord() for the per-face rotation.
    /// </summary>
    public static class TileStreamingManager
    {
        // ──────────────────────────────────────────────────────────────────────────────────
        // Configuration constants
        // ──────────────────────────────────────────────────────────────────────────────────

        // Levels 0..CoarseMaxLevel are always bootstrapped + pinned; only finer levels stream.
        public const int CoarseMaxLevel = 2;

        private const int MaxConcurrentLoads  = 8;  // in-flight KSPTextureLoader requests per body
        private const int MaxUploadsPerFrame  = 4;  // atlas uploads per body per frame
        private const int MetricsLogInterval  = 600; // frames between metric log lines (~10 s at 60 fps)

        private static readonly TextureLoadOptions StreamingOptions = new TextureLoadOptions
        {
            Linear     = false,
            Unreadable = true,
        };

        // Normal-map tangent data must not go through sRGB decode on upload.
        private static readonly TextureLoadOptions NormalStreamingOptions = new TextureLoadOptions
        {
            Linear     = true,
            Unreadable = true,
        };

        // ──────────────────────────────────────────────────────────────────────────────────
        // Per-body state
        // ──────────────────────────────────────────────────────────────────────────────────

        private class BodyStreamState
        {
            public ParallaxTerrainBody body;
            public string              sphereName;
            public VirtualTextureConfig cfg;

            // Tiles currently being loaded (to avoid duplicate requests)
            public HashSet<long> colorLoading  = new HashSet<long>();
            public HashSet<long> heightLoading = new HashSet<long>();
            public HashSet<long> normalLoading = new HashSet<long>();

            // Async loads in flight
            public List<InFlightTile> colorInFlight  = new List<InFlightTile>();
            public List<InFlightTile> heightInFlight = new List<InFlightTile>();
            public List<InFlightTile> normalInFlight = new List<InFlightTile>();

            // Pending load queue (rebuilt each frame, sorted by priority before processing)
            public List<PendingTile> colorQueue  = new List<PendingTile>();
            public List<PendingTile> heightQueue = new List<PendingTile>();
            public List<PendingTile> normalQueue = new List<PendingTile>();

            // Completed tiles waiting for upload
            public Queue<CompletedTile> colorCompleted  = new Queue<CompletedTile>();
            public Queue<CompletedTile> heightCompleted = new Queue<CompletedTile>();
            public Queue<CompletedTile> normalCompleted = new Queue<CompletedTile>();

            // Metrics
            public int tilesRequestedLastFrame;
            public int tilesLoadedLastFrame;
            public int evictionsLastFrame; // tracked indirectly via TileCache
            public int framesSinceLastLog;
        }

        private struct InFlightTile
        {
            public long                  key;
            public int                   face, level, tx, ty;
            public TextureHandle<Texture2D> handle;
            // State captured here so the on-completed lambda doesn't capture mutable locals.
        }

        private struct PendingTile
        {
            public long   key;
            public int    face, level, tx, ty;
            public int    priority; // lower = higher priority (finer level = lower priority number? no — load coarser first)
            public string rootPath;
        }

        private struct CompletedTile
        {
            public long                  key;
            public int                   face, level, tx, ty;
            public TextureHandle<Texture2D> handle;
        }

        // Reusable set for "required tiles this frame" to avoid allocating each call.
        private static readonly HashSet<long> s_RequiredScratch = new HashSet<long>();

        private static readonly Dictionary<string, BodyStreamState> s_Bodies
            = new Dictionary<string, BodyStreamState>();

        // ──────────────────────────────────────────────────────────────────────────────────
        // Registration
        // ──────────────────────────────────────────────────────────────────────────────────

        public static void RegisterBody(string sphereName, ParallaxTerrainBody body)
        {
            if (s_Bodies.ContainsKey(sphereName))
                return;

            s_Bodies[sphereName] = new BodyStreamState
            {
                body       = body,
                sphereName = sphereName,
                cfg        = body.virtualTextureConfig,
            };
            ParallaxDebug.Log($"TileStreamingManager: registered '{sphereName}'");
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // Debug / diagnostics
        // ──────────────────────────────────────────────────────────────────────────────────

        public struct BodyDebugInfo
        {
            public string sphereName;
            public int    colorSlots, colorTotal;
            public int    heightSlots, heightTotal;
            public int    normalSlots, normalTotal;
            public int[]  colorLevelCounts;   // length = maxLevel+1, or null
            public int[]  heightLevelCounts;
            public int[]  normalLevelCounts;
            public int    colorQueue,  colorFlight;
            public int    heightQueue, heightFlight;
            public int    normalQueue, normalFlight;
            public int    tilesRequested, tilesLoaded;
        }

        public static List<BodyDebugInfo> GetAllBodyDebugInfo()
        {
            var result = new List<BodyDebugInfo>(s_Bodies.Count);
            foreach (var kvp in s_Bodies)
            {
                var state  = kvp.Value;
                var color  = state.body.colorTileCache;
                var height = state.body.heightTileCache;
                var normal = state.body.normalTileCache;
                result.Add(new BodyDebugInfo
                {
                    sphereName        = state.sphereName,
                    colorSlots        = color  != null ? color.OccupiedSlots    : 0,
                    colorTotal        = color  != null ? color.TotalSlots        : 0,
                    heightSlots       = height != null ? height.OccupiedSlots   : 0,
                    heightTotal       = height != null ? height.TotalSlots       : 0,
                    normalSlots       = normal != null ? normal.OccupiedSlots   : 0,
                    normalTotal       = normal != null ? normal.TotalSlots       : 0,
                    colorLevelCounts  = color  != null ? color.GetLevelCounts()  : null,
                    heightLevelCounts = height != null ? height.GetLevelCounts() : null,
                    normalLevelCounts = normal != null ? normal.GetLevelCounts() : null,
                    colorQueue        = state.colorQueue.Count,
                    colorFlight       = state.colorInFlight.Count,
                    heightQueue       = state.heightQueue.Count,
                    heightFlight      = state.heightInFlight.Count,
                    normalQueue       = state.normalQueue.Count,
                    normalFlight      = state.normalInFlight.Count,
                    tilesRequested    = state.tilesRequestedLastFrame,
                    tilesLoaded       = state.tilesLoadedLastFrame,
                });
            }
            return result;
        }

        public static void UnregisterBody(string sphereName)
        {
            if (!s_Bodies.TryGetValue(sphereName, out var state))
                return;

            // Release any in-flight handles to avoid texture leaks.
            foreach (var t in state.colorInFlight)  t.handle.Dispose();
            foreach (var t in state.heightInFlight) t.handle.Dispose();
            foreach (var t in state.normalInFlight) t.handle.Dispose();
            while (state.colorCompleted.Count  > 0) state.colorCompleted.Dequeue().handle.Dispose();
            while (state.heightCompleted.Count > 0) state.heightCompleted.Dequeue().handle.Dispose();
            while (state.normalCompleted.Count > 0) state.normalCompleted.Dequeue().handle.Dispose();

            s_Bodies.Remove(sphereName);
            ParallaxDebug.Log($"TileStreamingManager: unregistered '{sphereName}'");
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // Per-frame update (called by RuntimeOperations.Update)
        // ──────────────────────────────────────────────────────────────────────────────────

        public static void Update(int frame)
        {
            foreach (var kvp in s_Bodies)
                UpdateBody(kvp.Value, frame);
        }

        private static void UpdateBody(BodyStreamState state, int frame)
        {
            TileCache colorCache  = state.body.colorTileCache;
            TileCache heightCache = state.body.heightTileCache;
            TileCache normalCache = state.body.normalTileCache;
            VirtualTextureConfig cfg = state.cfg;

            // ── Phase 1: determine required tiles from visible quads ──────────────────────

            s_RequiredScratch.Clear();
            CollectRequiredTiles(state.sphereName, cfg.maxLevel, s_RequiredScratch);
            state.tilesRequestedLastFrame = s_RequiredScratch.Count;

            // ── Phase 2: refresh LRU for already-resident tiles ───────────────────────────

            foreach (long key in s_RequiredScratch)
            {
                colorCache?.MarkTileUsed(key, frame);
                heightCache?.MarkTileUsed(key, frame);
                normalCache?.MarkTileUsed(key, frame);
            }

            // ── Phase 3: build per-cache pending queues ───────────────────────────────────

            state.colorQueue.Clear();
            state.heightQueue.Clear();
            state.normalQueue.Clear();

            foreach (long key in s_RequiredScratch)
            {
                if (colorCache != null && !colorCache.IsTileResident(key) && !state.colorLoading.Contains(key))
                {
                    TileCache.UnpackKey(key, out int face, out int level, out int tx, out int ty);
                    state.colorQueue.Add(new PendingTile
                    {
                        key = key, face = face, level = level, tx = tx, ty = ty,
                        priority = level,       // lower level number = coarser = higher priority
                        rootPath = cfg.colormapTilePath,
                    });
                }
                if (heightCache != null && !heightCache.IsTileResident(key) && !state.heightLoading.Contains(key))
                {
                    TileCache.UnpackKey(key, out int face, out int level, out int tx, out int ty);
                    state.heightQueue.Add(new PendingTile
                    {
                        key = key, face = face, level = level, tx = tx, ty = ty,
                        priority = level,
                        rootPath = cfg.heightmapTilePath,
                    });
                }
                if (normalCache != null && !normalCache.IsTileResident(key) && !state.normalLoading.Contains(key))
                {
                    TileCache.UnpackKey(key, out int face, out int level, out int tx, out int ty);
                    state.normalQueue.Add(new PendingTile
                    {
                        key = key, face = face, level = level, tx = tx, ty = ty,
                        priority = level,
                        rootPath = cfg.normalmapTilePath,
                    });
                }
            }

            // Sort: coarser first (smaller level = lower priority value = higher importance).
            state.colorQueue.Sort( (a, b) => a.priority.CompareTo(b.priority));
            state.heightQueue.Sort((a, b) => a.priority.CompareTo(b.priority));
            state.normalQueue.Sort((a, b) => a.priority.CompareTo(b.priority));

            // ── Phase 4: start new async loads ────────────────────────────────────────────

            StartLoads(state.colorQueue,  state.colorInFlight,  state.colorLoading,  state.colorCompleted,  StreamingOptions);
            StartLoads(state.heightQueue, state.heightInFlight, state.heightLoading, state.heightCompleted, StreamingOptions);
            StartLoads(state.normalQueue, state.normalInFlight, state.normalLoading, state.normalCompleted, NormalStreamingOptions);

            // ── Phase 5: tick in-flight handles and upload completed tiles ────────────────

            int uploaded = 0;
            DrainInFlight(state.colorInFlight,  state.colorLoading,  state.colorCompleted,
                          colorCache,  frame, ref uploaded);
            DrainInFlight(state.heightInFlight, state.heightLoading, state.heightCompleted,
                          heightCache, frame, ref uploaded);
            DrainInFlight(state.normalInFlight, state.normalLoading, state.normalCompleted,
                          normalCache, frame, ref uploaded);

            // ── Phase 6: flush page tables ────────────────────────────────────────────────

            colorCache?.ApplyPageTable();
            heightCache?.ApplyPageTable();
            normalCache?.ApplyPageTable();

            state.tilesLoadedLastFrame = uploaded;

            // ── Metrics (logged every MetricsLogInterval frames) ─────────────────────────

            state.framesSinceLastLog++;
            if (state.framesSinceLastLog >= MetricsLogInterval)
            {
                state.framesSinceLastLog = 0;
                int colorOccupancy  = colorCache  != null ? colorCache.OccupiedSlots  : 0;
                int colorTotal      = colorCache  != null ? colorCache.TotalSlots      : 0;
                int heightOccupancy = heightCache != null ? heightCache.OccupiedSlots  : 0;
                int heightTotal     = heightCache != null ? heightCache.TotalSlots     : 0;
                int normalOccupancy = normalCache != null ? normalCache.OccupiedSlots  : 0;
                int normalTotal     = normalCache != null ? normalCache.TotalSlots     : 0;
                ParallaxDebug.Log(
                    $"[VT Stream] {state.sphereName}  " +
                    $"req={state.tilesRequestedLastFrame}  loaded={state.tilesLoadedLastFrame}  " +
                    $"colorSlots={colorOccupancy}/{colorTotal}  heightSlots={heightOccupancy}/{heightTotal}  normalSlots={normalOccupancy}/{normalTotal}  " +
                    $"colorQueue={state.colorQueue.Count}  heightQueue={state.heightQueue.Count}  normalQueue={state.normalQueue.Count}  " +
                    $"colorFlight={state.colorInFlight.Count}  heightFlight={state.heightInFlight.Count}  normalFlight={state.normalInFlight.Count}");
            }
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // Required-tile collection
        // ──────────────────────────────────────────────────────────────────────────────────

        private static void CollectRequiredTiles(string sphereName, int maxLevel, HashSet<long> required)
        {
            // Fine tiles come from visible, leaf (non-subdivided) quads. For each leaf we also
            // queue every coarser ancestor down to CoarseMaxLevel+1 so the shader's per-pixel
            // walk has loaded tiles to gradient through — otherwise it falls straight from the
            // leaf level to the pinned L2 floor, producing a visible quality cliff.
            // Coarse ancestors are shared across neighbouring leaves (4 L6 leaves share one L5
            // ancestor, 16 share one L4, etc.), so the unique tile count grows ~1.33x rather
            // than linearly per extra level.
            foreach (PQ quad in PQSMod_Parallax.terrainQuadData.Keys)
            {
                if (quad.sphereRoot.name != sphereName) continue;
                if (!quad.isVisible || quad.isSubdivided)  continue;

                int leafLevel = Mathf.Min(quad.subdivision, maxLevel);
                if (leafLevel <= CoarseMaxLevel) continue; // coarse levels are always pinned

                int face = (int)quad.plane;
                for (int level = leafLevel; level > CoarseMaxLevel; level--)
                {
                    GetCorrectedTileCoord(face, quad.uvSW.x, quad.uvSW.y, level, out int tx, out int ty);
                    required.Add(TileCache.PackKey(face, level, tx, ty));
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // Load dispatch
        // ──────────────────────────────────────────────────────────────────────────────────

        private static void StartLoads(
            List<PendingTile>          queue,
            List<InFlightTile>         inFlight,
            HashSet<long>              loading,
            Queue<CompletedTile>       completed,
            TextureLoadOptions         options)
        {
            int slots = MaxConcurrentLoads - inFlight.Count;
            for (int i = 0; i < queue.Count && slots > 0; i++)
            {
                var p = queue[i];
                if (loading.Contains(p.key)) continue;

                string path = TilePath(p.rootPath, p.face, p.level, p.tx, p.ty);
                if (!TextureLoader.TextureExists(path))
                {
                    // Tile missing on disk — mark as "loaded" (not-present) so we don't keep trying.
                    loading.Add(p.key);
                    continue;
                }

                var handle = TextureLoader.LoadTexture<Texture2D>(path, options);
                loading.Add(p.key);

                // Capture values for the completion lambda.
                long   key   = p.key;
                int    face  = p.face, level = p.level, tx = p.tx, ty = p.ty;

                // If the load completes synchronously (cached result), the callback fires immediately inside
                // LoadTexture — we still enqueue to the completed queue so the upload happens in a
                // controlled slot on the same code path.
                var capturedHandle = handle; // separate reference so the lambda owns it
                handle.OnCompleted += _ =>
                {
                    completed.Enqueue(new CompletedTile
                    {
                        key = key, face = face, level = level, tx = tx, ty = ty,
                        handle = capturedHandle,
                    });
                };
                handle.OnError += (_, ex) =>
                {
                    ParallaxDebug.LogError($"TileStreamingManager: tile load failed for {path}: {ex.Message}");
                    loading.Remove(key);
                    capturedHandle.Dispose();
                };

                inFlight.Add(new InFlightTile { key = key, face = face, level = level, tx = tx, ty = ty, handle = handle });
                slots--;
            }
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // In-flight ticking + upload
        // ──────────────────────────────────────────────────────────────────────────────────

        private static void DrainInFlight(
            List<InFlightTile>   inFlight,
            HashSet<long>        loading,
            Queue<CompletedTile> completed,
            TileCache            cache,
            int                  frame,
            ref int              uploaded)
        {
            // Tick all in-flight handles to let the loader make progress; completed ones move to the
            // completed queue via the OnCompleted callback registered in StartLoads.
            for (int i = inFlight.Count - 1; i >= 0; i--)
            {
                var tile = inFlight[i];
                if (!tile.handle.IsComplete)
                    tile.handle.Tick();

                if (tile.handle.IsComplete || tile.handle.IsError)
                {
                    // If completed successfully the OnCompleted lambda already enqueued it.
                    // If errored the OnError lambda already cleaned up.
                    inFlight.RemoveAt(i);
                }
            }

            // Upload completed tiles up to the per-frame budget.
            while (uploaded < MaxUploadsPerFrame && completed.Count > 0)
            {
                var c = completed.Dequeue();
                loading.Remove(c.key);

                if (cache == null) { c.handle.Dispose(); continue; }

                Texture2D tex;
                try { tex = c.handle.GetTexture(); }
                catch (Exception e)
                {
                    ParallaxDebug.LogError($"TileStreamingManager: GetTexture failed for L{c.level} face{c.face} {c.tx},{c.ty}: {e.Message}");
                    c.handle.Dispose(); continue;
                }

                bool ok = cache.TryUploadTile(c.face, c.level, c.tx, c.ty, tex, frame);
                c.handle.Dispose();
                if (ok) uploaded++;
            }
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // UV coordinate helpers
        // ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Convert a quad's raw face-UV origin (uvSW.x/y) to the tile coordinate in corrected
        /// UV space.  The shader samples the page table using corrected UVs (post CorrectFaceUV),
        /// so tile keys must be in the same space.
        ///
        /// Rotations mirror ParallaxUtils.cginc:CorrectFaceUV:
        ///   face 0 (Xp) 90° CCW:  corr = (v, 1-u)
        ///   face 1 (Xn) 90° CW:   corr = (1-v, u)
        ///   face 2,3,4: 180°:     corr = (1-u, 1-v)
        ///   face 5 (Zn): identity
        /// </summary>
        public static void GetCorrectedTileCoord(int face, float uvSwX, float uvSwY, int tileLevel,
                                                  out int tx, out int ty)
        {
            int g = 1 << tileLevel;
            // Tile coords in raw UV space.
            int rawX = Mathf.Clamp(Mathf.FloorToInt(uvSwX * g), 0, g - 1);
            int rawY = Mathf.Clamp(Mathf.FloorToInt(uvSwY * g), 0, g - 1);

            switch (face)
            {
                case 0: // Xp: 90° CCW — corrected SW becomes (rawY, g-1-rawX)
                    tx = rawY;
                    ty = g - 1 - rawX;
                    break;
                case 1: // Xn: 90° CW — corrected SW becomes (g-1-rawY, rawX)
                    tx = g - 1 - rawY;
                    ty = rawX;
                    break;
                case 2: case 3: case 4: // 180° — corrected SW becomes (g-1-rawX, g-1-rawY)
                    tx = g - 1 - rawX;
                    ty = g - 1 - rawY;
                    break;
                default: // face 5 (Zn): identity
                    tx = rawX;
                    ty = rawY;
                    break;
            }
        }

        private static readonly string[] FaceNames = { "Xp", "Xn", "Yp", "Yn", "Zp", "Zn" };

        private static string TilePath(string rootPath, int face, int level, int tx, int ty)
            => $"{rootPath}/{FaceNames[face]}/level_{level}/tile_{tx}_{ty}.dds";
    }
}
