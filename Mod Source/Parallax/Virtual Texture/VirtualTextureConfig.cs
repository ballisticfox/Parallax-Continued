namespace Parallax
{
    /// <summary>
    /// Per-body settings for the virtual texture cache. Populated by ConfigLoader from a
    /// "VirtualTexture" subnode under ParallaxTerrain.Body.
    ///
    /// Example config:
    ///   ParallaxTerrain
    ///   {
    ///       Body
    ///       {
    ///           name = Earth
    ///           VirtualTexture
    ///           {
    ///               colormapTilePath = Sol-Textures/PluginData/03_Earth-System/03_Earth/Terrain/Color
    ///               heightmapTilePath = Sol-Textures/PluginData/03_Earth-System/03_Earth/Terrain/Height
    ///               atlasSize = 8192    // optional, default 8192 (applies to both caches)
    ///               tileSize = 256      // optional, default 256  (must match slice_tiles.py output)
    ///               borderPx = 4        // optional, default 4    (must match slice_tiles.py output)
    ///               maxLevel = 3        // optional, default 3    (page-table size + deepest level preloaded)
    ///           }
    ///       }
    ///   }
    ///
    /// Either path can be omitted to disable that cache. The colormap and heightmap get separate TileCache
    /// instances (separate atlas + page table) but share the dimension settings for simplicity. If you need
    /// per-cache dimensions later we can split them out.
    /// </summary>
    public class VirtualTextureConfig
    {
        // Path passed to KSPTextureLoader for each tile (face/level/tile_x_y.dds is appended).
        // Resolves the same way as any other Parallax shader texture path.
        public string colormapTilePath;
        public string heightmapTilePath;

        public int atlasSize = 8192;
        public int tileSize = 256;
        public int borderPx = 4;
        public int maxLevel = 3;

        public bool HasColormap => !string.IsNullOrEmpty(colormapTilePath);
        public bool HasHeightmap => !string.IsNullOrEmpty(heightmapTilePath);
        public bool IsValid => HasColormap || HasHeightmap;
    }
}
