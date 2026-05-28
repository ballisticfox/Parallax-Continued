using System;
using System.Collections.Generic;
using UnityEngine;
using KSPTextureLoader;

namespace Parallax
{
    /// <summary>
    /// Virtual texture cache atlas (Stage 2 — streaming).
    ///
    /// Manages a fixed-size atlas + RGBA32 page table on the GPU.
    /// Coarse levels (0..CoarseMaxLevel) are preloaded synchronously at body-load time and pinned so
    /// they are never evicted.  Fine levels are populated on demand by TileStreamingManager, which calls
    /// TryUploadTile() as tiles complete loading and EvictLRU implicitly when the atlas is full.
    ///
    /// Caller contract:
    ///   1. Construct, call BootstrapCoarseLevels, then BindToMaterial once.
    ///   2. Each frame TileStreamingManager calls TryUploadTile (which may evict) then ApplyPageTable.
    ///   3. On body unload call Dispose.
    ///
    /// Atlas layout: (tileSize + 2*borderPx) px slots packed left-to-right, top-to-bottom.
    /// Page table:   RGBA32 — R=slotX, G=slotY, A=255 if loaded, 0 if empty.
    ///               texelX = face * faceStride + tileX_corrected
    ///               texelY = (1 << level) - 1   + tileY_corrected
    /// where tileX/Y are in CORRECTED UV space (post CorrectFaceUV), matching the shader lookup.
    /// </summary>
    public class TileCache : IDisposable
    {
        // Face name ordering matches PQSMod_PlanetUV: (int)quad.plane gives the index.
        private static readonly string[] FaceNames = { "Xp", "Xn", "Yp", "Yn", "Zp", "Zn" };

        public Texture2D Atlas    { get; private set; }
        public Texture2D PageTable { get; private set; }

        public readonly int atlasSize;
        public readonly int tileSize;
        public readonly int borderPx;
        public readonly int maxLevel;

        public int SlotSize    => tileSize + 2 * borderPx;
        public int SlotsPerRow => atlasSize / SlotSize;
        public int TotalSlots  => SlotsPerRow * SlotsPerRow;

        // (face, level, corrected_tileX, corrected_tileY) → linear slot index
        private readonly Dictionary<long, int> slotMap = new Dictionary<long, int>();

        // Per-slot metadata
        private long[] slotOwner; // SLOT_FREE, SLOT_PINNED, or packed tile key
        private int[]  slotFrame; // Time.frameCount when this slot was last requested

        private const long SLOT_FREE   = long.MinValue;
        private const long SLOT_PINNED = long.MinValue + 1;

        // Working copy of the page table pixel array — updated incrementally, flushed once per frame.
        private Color32[] pagePixels;
        private bool pageTableDirty;

        // ──────────────────────────────────────────────────────────────────────────────────
        // Construction
        // ──────────────────────────────────────────────────────────────────────────────────

        public TileCache(int atlasSize, int tileSize, int borderPx, int maxLevel)
        {
            if (atlasSize <= 0 || tileSize <= 0 || maxLevel < 0)
                throw new ArgumentException($"TileCache: invalid args atlasSize={atlasSize} tileSize={tileSize} maxLevel={maxLevel}");

            this.atlasSize = atlasSize;
            this.tileSize  = tileSize;
            this.borderPx  = borderPx;
            this.maxLevel  = maxLevel;

            int total = TotalSlots;
            slotOwner = new long[total];
            slotFrame = new int[total];
            for (int i = 0; i < total; i++)
                slotOwner[i] = SLOT_FREE;

            // Atlas allocation is deferred until the first tile arrives so we can match its format.

            int ptW = 6 * (1 << maxLevel);
            int ptH = (1 << (maxLevel + 1)) - 1;
            pagePixels = new Color32[ptW * ptH]; // zero-initialised = all empty (a=0)

            PageTable = new Texture2D(ptW, ptH, TextureFormat.RGBA32, false, true);
            PageTable.name      = "TileCachePageTable";
            PageTable.wrapMode  = TextureWrapMode.Clamp;
            PageTable.filterMode = FilterMode.Point;
            PageTable.SetPixels32(pagePixels);
            PageTable.Apply(false, false);
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // Bootstrap (called once at body load, blocks until done)
        // ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Synchronously loads all tiles from level 0 through coarseMaxLevel and pins them so they
        /// are never evicted by the streaming system.  These tiles provide the coarse fallback that
        /// the shader walks up to when a fine tile is not yet resident.
        /// </summary>
        public void BootstrapCoarseLevels(string rootPath, int coarseMaxLevel)
        {
            coarseMaxLevel = Mathf.Clamp(coarseMaxLevel, 0, maxLevel);

            var options = new TextureLoadOptions { Linear = false, Unreadable = true };

            var requests = new List<TileRequest>();
            for (int face = 0; face < 6; face++)
            {
                for (int level = 0; level <= coarseMaxLevel; level++)
                {
                    int g = 1 << level;
                    for (int ty = 0; ty < g; ty++)
                    {
                        for (int tx = 0; tx < g; tx++)
                        {
                            string path = TilePath(rootPath, face, level, tx, ty);
                            if (!TextureLoader.TextureExists(path)) continue;
                            var handle = TextureLoader.LoadTexture<Texture2D>(path, options);
                            requests.Add(new TileRequest { face = face, level = level, tx = tx, ty = ty, handle = handle, path = path });
                        }
                    }
                }
            }

            int loaded = 0, failed = 0;
            foreach (var req in requests)
            {
                Texture2D tile;
                try { tile = req.handle.GetTexture(); }
                catch (Exception e)
                {
                    ParallaxDebug.LogError($"TileCache: bootstrap failed for {req.path}: {e.Message}");
                    req.handle.Dispose(); failed++; continue;
                }

                if (!EnsureAtlasAllocated(tile, req.path) || !ValidateTile(tile, req.path))
                { req.handle.Dispose(); failed++; continue; }

                int slot = AllocateAnyFreeSlot();
                if (slot < 0)
                { ParallaxDebug.LogError($"TileCache: atlas full during bootstrap, dropping {req.path}"); req.handle.Dispose(); failed++; continue; }

                CopyTileToSlot(tile, slot);
                long key = PackKey(req.face, req.level, req.tx, req.ty);
                slotMap[key]    = slot;
                slotOwner[slot] = SLOT_PINNED;
                slotFrame[slot] = int.MaxValue; // pinned tiles appear perpetually fresh
                SetPageTableTexel(req.face, req.level, req.tx, req.ty, slot);

                req.handle.Dispose();
                loaded++;
            }

            ApplyPageTable();
            ParallaxDebug.Log($"TileCache: bootstrapped {loaded} coarse tiles ({failed} failed/missing) from {rootPath}");
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // Streaming API (called each frame by TileStreamingManager)
        // ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Upload a completed streaming tile into the atlas.  Evicts the least-recently-used
        /// non-pinned slot if the atlas is full.  Returns false if upload fails (format mismatch,
        /// wrong size, or no evictable slot).
        /// </summary>
        public bool TryUploadTile(int face, int level, int tx, int ty, Texture2D tile, int frame)
        {
            if (!EnsureAtlasAllocated(tile, $"streaming L{level} face{face} {tx},{ty}")) return false;
            if (!ValidateTile(tile, $"streaming L{level} face{face} {tx},{ty}"))          return false;

            int slot = AllocateStreamingSlot(frame, out long evictedKey);

            if (slot < 0)
            {
                ParallaxDebug.LogError($"TileCache: no evictable slot for L{level} face{face} {tx},{ty}");
                return false;
            }

            // If we recycled a slot, remove its old page-table entry.
            if (evictedKey != SLOT_FREE && evictedKey != SLOT_PINNED)
            {
                slotMap.Remove(evictedKey);
                ClearPageTableTexel(evictedKey);
            }

            CopyTileToSlot(tile, slot);
            long key = PackKey(face, level, tx, ty);
            slotMap[key]    = slot;
            slotOwner[slot] = key;
            slotFrame[slot] = frame;
            SetPageTableTexel(face, level, tx, ty, slot);
            return true;
        }

        /// <summary>Flush incremental page-table changes to the GPU.  Call once per frame.</summary>
        public void ApplyPageTable()
        {
            if (!pageTableDirty) return;
            PageTable.SetPixels32(pagePixels);
            PageTable.Apply(false, false);
            pageTableDirty = false;
        }

        public bool IsTileResident(long key) => slotMap.ContainsKey(key);

        public bool IsTileResident(int face, int level, int tx, int ty)
            => slotMap.ContainsKey(PackKey(face, level, tx, ty));

        /// <summary>Refresh the LRU timestamp so this slot won't be evicted while the tile is needed.</summary>
        public void MarkTileUsed(long key, int frame)
        {
            if (slotMap.TryGetValue(key, out int slot))
                slotFrame[slot] = frame;
        }

        public int OccupiedSlots => slotMap.Count;

        /// <summary>Returns tile counts per level, indexed 0..maxLevel inclusive.</summary>
        public int[] GetLevelCounts()
        {
            var counts = new int[maxLevel + 1];
            foreach (var kvp in slotMap)
            {
                UnpackKey(kvp.Key, out _, out int level, out _, out _);
                if ((uint)level <= (uint)maxLevel)
                    counts[level]++;
            }
            return counts;
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // Material binding
        // ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Bind atlas + page table + metadata uniforms to a material.  uniformPrefix is e.g. "_Color"
        /// or "_Height".  Call once per material variant after BootstrapCoarseLevels.
        /// </summary>
        public void BindToMaterial(Material mat, string uniformPrefix)
        {
            mat.SetTexture(uniformPrefix + "TileAtlas",   Atlas);
            mat.SetTexture(uniformPrefix + "PageTable",   PageTable);
            mat.SetFloat(uniformPrefix + "TileAtlasSize", atlasSize);
            mat.SetFloat(uniformPrefix + "TileSize",      tileSize);
            mat.SetFloat(uniformPrefix + "TileBorder",    borderPx);
            mat.SetFloat(uniformPrefix + "MaxTileLevel",  maxLevel);
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // Key packing helpers (public so TileStreamingManager can use them)
        // ──────────────────────────────────────────────────────────────────────────────────

        // Layout: tileX [0..15], tileY [16..31], level [32..39], face [40..47]
        public static long PackKey(int face, int level, int tx, int ty)
            => ((long)face << 40) | ((long)level << 32) | ((long)(ty & 0xFFFF) << 16) | (long)(tx & 0xFFFF);

        public static void UnpackKey(long key, out int face, out int level, out int tx, out int ty)
        {
            tx    = (int)(key & 0xFFFF);
            ty    = (int)((key >> 16) & 0xFFFF);
            level = (int)((key >> 32) & 0xFF);
            face  = (int)((key >> 40) & 0xFF);
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // Dispose
        // ──────────────────────────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (Atlas     != null) { UnityEngine.Object.Destroy(Atlas);     Atlas     = null; }
            if (PageTable != null) { UnityEngine.Object.Destroy(PageTable); PageTable = null; }
            slotMap.Clear();
        }

        // ──────────────────────────────────────────────────────────────────────────────────
        // Private helpers
        // ──────────────────────────────────────────────────────────────────────────────────

        private static string TilePath(string rootPath, int face, int level, int tx, int ty)
            => $"{rootPath}/{FaceNames[face]}/level_{level}/tile_{tx}_{ty}.dds";

        private bool EnsureAtlasAllocated(Texture2D firstTile, string debugPath)
        {
            if (Atlas != null) return true;

            int blockSize = CompressedBlockSize(firstTile.format);
            if (blockSize > 1 && SlotSize % blockSize != 0)
            {
                ParallaxDebug.LogError($"TileCache: {firstTile.format} block size {blockSize} doesn't divide slot size {SlotSize} — use aligned tile/border or uncompressed format. ({debugPath})");
                return false;
            }

            Atlas = new Texture2D(atlasSize, atlasSize, firstTile.format, false, false);
            Atlas.name       = "TileCacheAtlas";
            Atlas.wrapMode   = TextureWrapMode.Clamp;
            Atlas.filterMode = FilterMode.Bilinear;
            return true;
        }

        private bool ValidateTile(Texture2D tile, string debugPath)
        {
            if (tile.width != SlotSize || tile.height != SlotSize)
            {
                ParallaxDebug.LogError($"TileCache: tile {debugPath} is {tile.width}x{tile.height}, expected {SlotSize}x{SlotSize}");
                return false;
            }
            if (tile.format != Atlas.format)
            {
                ParallaxDebug.LogError($"TileCache: tile {debugPath} format {tile.format} ≠ atlas {Atlas.format}");
                return false;
            }
            return true;
        }

        private void CopyTileToSlot(Texture2D tile, int slot)
        {
            int slotX = slot % SlotsPerRow;
            int slotY = slot / SlotsPerRow;
            Graphics.CopyTexture(tile, 0, 0, 0, 0, SlotSize, SlotSize,
                                  Atlas, 0, 0, slotX * SlotSize, slotY * SlotSize);
        }

        // Finds any free slot (no eviction). Returns -1 if atlas is completely full.
        private int AllocateAnyFreeSlot()
        {
            int total = TotalSlots;
            for (int i = 0; i < total; i++)
                if (slotOwner[i] == SLOT_FREE) return i;
            return -1;
        }

        // Finds a free slot or evicts the LRU non-pinned slot. evictedKey receives the key that was removed.
        private int AllocateStreamingSlot(int frame, out long evictedKey)
        {
            evictedKey = SLOT_FREE;

            // Prefer a genuinely free slot first.
            int slot = AllocateAnyFreeSlot();
            if (slot >= 0) return slot;

            // Evict the least-recently-used non-pinned slot.
            int   oldestFrame = int.MaxValue;
            int   lruSlot     = -1;
            int   total       = TotalSlots;
            for (int i = 0; i < total; i++)
            {
                if (slotOwner[i] == SLOT_FREE || slotOwner[i] == SLOT_PINNED) continue;
                if (slotFrame[i] < oldestFrame)
                {
                    oldestFrame = slotFrame[i];
                    lruSlot     = i;
                }
            }

            if (lruSlot < 0) return -1; // every slot is pinned — shouldn't happen with a sensibly sized atlas

            evictedKey         = slotOwner[lruSlot];
            slotOwner[lruSlot] = SLOT_FREE;
            slotFrame[lruSlot] = 0;
            return lruSlot;
        }

        private void SetPageTableTexel(int face, int level, int tx, int ty, int slot)
        {
            int slotX = slot % SlotsPerRow;
            int slotY = slot / SlotsPerRow;
            if (slotX > 255 || slotY > 255)
            {
                ParallaxDebug.LogError($"TileCache: slot {slotX},{slotY} exceeds byte range — atlas too large for byte-packed page table");
                return;
            }
            int faceStride = 1 << maxLevel;
            int texelX = face * faceStride + tx;
            int texelY = (1 << level) - 1 + ty;
            pagePixels[texelY * PageTable.width + texelX] = new Color32((byte)slotX, (byte)slotY, 0, 255);
            pageTableDirty = true;
        }

        private void ClearPageTableTexel(long key)
        {
            UnpackKey(key, out int face, out int level, out int tx, out int ty);
            int faceStride = 1 << maxLevel;
            int texelX = face * faceStride + tx;
            int texelY = (1 << level) - 1 + ty;
            pagePixels[texelY * PageTable.width + texelX] = new Color32(0, 0, 0, 0);
            pageTableDirty = true;
        }

        private static int CompressedBlockSize(TextureFormat fmt)
        {
            switch (fmt)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT5:
                case TextureFormat.BC4:
                case TextureFormat.BC5:
                case TextureFormat.BC6H:
                case TextureFormat.BC7:
                    return 4;
                default:
                    return 1;
            }
        }

        private struct TileRequest
        {
            public int face, level, tx, ty;
            public string path;
            public TextureHandle<Texture2D> handle;
        }
    }
}
