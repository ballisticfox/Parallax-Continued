// Samplers
#if !defined (SCALED)

sampler2D _MainTexLow;
sampler2D _BumpMapLow;

sampler2D _MainTexMid;
sampler2D _BumpMapMid;

sampler2D _MainTexHigh;
sampler2D _BumpMapHigh;

sampler2D _MainTexSteep;
sampler2D _BumpMapSteep;

sampler2D _DisplacementMap;
sampler2D _InfluenceMap;

// Virtual texture pyramid for the planet COLORMAP (fragment-shader sample).
// Atlas: cache Texture2D holding loaded tiles packed into a grid of (_ColorTileSize + 2*_ColorTileBorder) slots.
// PageTable: small Texture2D where each texel = (slotX, slotY, _, loaded) for one (face, level, tileX, tileY).
//            Layout: levels stacked vertically (L0=row 0, L1=rows 1-2, ...); faces tiled horizontally with
//            stride (1 << _ColorMaxTileLevel).
sampler2D _ColorTileAtlas;
sampler2D _ColorPageTable;
float _ColorTileAtlasSize;
float _ColorTileSize;
float _ColorTileBorder;
float _ColorMaxTileLevel;

// Virtual texture pyramid for the planet HEIGHTMAP (vertex-stage sample for GPU displacement).
// Same layout convention as the colormap above; bound to its own atlas + page table so the two can have
// different formats (e.g. BC4 heightmap vs BC1/RGBA color).
sampler2D _HeightTileAtlas;
sampler2D _HeightPageTable;
float _HeightTileAtlasSize;
float _HeightTileSize;
float _HeightTileBorder;
float _HeightMaxTileLevel;

float _HeightScale;
float _HeightOffset;
float _NearFieldEnd;
float _BlendWidth;

// Virtual texture pyramid for the planet NORMAL MAP (fragment-stage sample, tangent-space encoding).
// Same layout/format conventions as the colormap pyramid; loaded with Linear=true so the GPU doesn't
// sRGB-decode the tangent data on upload. _HasNormalVT is set to 1 by C# when this body has a normal
// cache bound; 0 otherwise so the shader can early-out and keep using i.worldNormal.
sampler2D _NormalTileAtlas;
sampler2D _NormalPageTable;
float _NormalTileAtlasSize;
float _NormalTileSize;
float _NormalTileBorder;
float _NormalMaxTileLevel;
float _HasNormalVT;

#if defined (AMBIENT_OCCLUSION)
    sampler2D _OcclusionMap;
#endif

#else

Texture2D _MainTexLow;
Texture2D _BumpMapLow;

Texture2D _MainTexMid;
Texture2D _BumpMapMid;

Texture2D _MainTexHigh;
Texture2D _BumpMapHigh;

Texture2D _MainTexSteep;
Texture2D _BumpMapSteep;

Texture2D _DisplacementMap;
Texture2D _InfluenceMap;

#if defined (AMBIENT_OCCLUSION)
    Texture2D _OcclusionMap;
#endif

#endif



float2 _MainTex_ST;

float _BiplanarBlendFactor;
float _Tiling;

float _DisplacementScale;
float _DisplacementOffset;

float _BumpScale;

// Slope params
float _SteepPower;
float _SteepContrast;
float _SteepMidpoint;

// Tessellation params
float _MaxTessellation;
float _TessellationEdgeLength;
float _MaxTessellationRange;
// Distance over which the tiling _DisplacementMap contributes to vertex displacement. Decoupled from
// _MaxTessellationRange so the GPU heightmap path can extend tessellation to km scale without dragging
// tile-level vertex bumps (which visibly shift at biplanar mip transitions) along with it.
float _TileDisplacementRange;

// Emission
float3 _EmissionColor;

//
// Other / game params
//

float3 _TerrainShaderOffset;
float3 _PlanetOrigin;
float _PlanetRadius;
float _PlanetOpacity;

// Conditional params

float _LowMidBlendStart;
float _LowMidBlendEnd;

float _MidHighBlendStart;
float _MidHighBlendEnd;

// VT debug visualisation mode (set globally from C# debug UI):
//   0 = resident level — the level the walk actually lands on (default)
//   1 = desired level  — what the shader's screen-space-derivative formula asks for
float _VTDebugMode;
