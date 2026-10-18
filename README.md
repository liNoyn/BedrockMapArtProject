# Bedrock Map Art Project (BMAP)

**English** | [日本語](README_JP.md)

> **🚧 Work in progress.** Everything works, but nothing has been verified in the game yet
> (see "Verification status" below). There is no release yet; build from source.

A Windows desktop app that turns images into map art for **Minecraft Bedrock Edition**.
It rebuilds the features and the screen layout of [MapartCraft](https://github.com/rebane2001/mapartcraft) (a web app for
Java Edition) for Bedrock: the image is reduced to block map colours and exported as **`.mcstructure` files** (64 × 64
pieces for structure blocks, one per map, or one file) with a build guide (textured placement plans, row-by-row steps,
material CSV and world coordinates).

The window has MapartCraft's three columns: **block selection** on the left, the **map preview** in the middle (map
colours, original, difference, shading analysis, 2D / 3D build views, height map) and **map settings**, export buttons,
the **shading & build check** and the **materials** on the right. The UI is in English and Japanese (switch at the top right).

Built with C# / .NET 8 / Avalonia 11 and SkiaSharp. Little-endian NBT, `.mcstructure`, all dithering and the 3D software
renderer are implemented in the project without further libraries.

> Renamed in v0.4.0 from **Bedrock Map Art Generator (BMAG)** to **Bedrock Map Art Project (BMAP)**. Settings, presets and
> custom blocks in `%APPDATA%\BMAG` are copied to `%APPDATA%\BMAP` on first start, and `*.bmag.json` projects still open.

## Features

**Block selection (as in MapartCraft)**
- 79 colour sets with 355 blocks: one per map colour (the Bedrock equivalents of every MapartCraft block) plus
  Bedrock-only colours from gd-codes' data (birch and spruce leaves, sculk, 16 wool slab colours — the wool slabs are not
  selected by default because they are unverified). Pick
  **any number** of blocks per colour; each row shows the dark / normal / light map colour and how many are selected.
- Several blocks of one colour share its pixels equally (same map, materials spread out), either in stretches (easy to
  build) or scattered.
- Block identifiers and states are validated against Mojang's block metadata (Bedrock 1.26); all states are written.
- Presets (one per colour, survival, survival – all, no support needed, all blocks, carpets, concrete, wool, greyscale,
  none) and your own presets.
- Custom blocks: by hand or from a behavior pack's `minecraft:map_color` (folder, `.mcpack`, `.mcaddon`).
- **Block icons** from the Minecraft textures. The first build downloads them from Mojang's official
  [bedrock-samples](https://github.com/Mojang/bedrock-samples) into `.textures/` and copies them next to `BMAP.exe`, so icons
  are there from the first start (Minecraft EULA applies; the textures are not part of this repository). Offline builds
  can fetch them from the app; a resource pack folder can be used too.

**Conversion**
- Images: PNG, JPEG, WebP, BMP, GIF, TIFF; frames of animated GIF / WebP and multi-page TIFF; video frames with ffmpeg.
- Crop (off / centre / manual, also by dragging), fit / fill / stretch, Lanczos / bilinear / nearest, brightness,
  contrast, saturation, gamma, sharpen. Transparency: blend, dither, or keep (no block).
- Colour distance: Oklab (default), CIELAB ΔE76, CIEDE2000, weighted RGB, RGB. Resizing in linear light.
- **Dithering**
  - Error diffusion: Floyd–Steinberg, Jarvis–Judice–Ninke, Burkes, Sierra Lite, Stucki, Atkinson. The error is diffused
    **in linear light** (default), so dithered areas keep their brightness when seen from a distance; serpentine scan and
    adjustable strength.
  - **Knoll pattern dithering** (Thomas Knoll's method used in Photoshop) with a Bayer 8 × 8 or blue-noise matrix: every
    colour is approximated by a mix of map colours, laid out as a regular pattern. 35–41 % lower colour error than Bayer.
  - Ordered: blue noise 64 × 64 (generated with void-and-cluster), Bayer 2 × 2 / 4 × 4 / 8 × 8, ordered 3 × 3.
  - All methods are compared in [docs/benchmarks.md](docs/benchmarks.md).

**3D map art (staircasing) and the shading rule**
- 3D: off (flat), Classic, Valley, full dark, full light.
- **Shading model**: by default the rule measured on Bedrock 1.26.51 (±1 alternates by coordinate parity, ±2 or more
  always). With the build aligned to the map grid, single steps are used where the parity allows, 23–27 % lower than
  always stepping by 2. Step 1 (Java), step 2 (gd-codes, Nukkit) and others can be chosen.
- **Shading rule analysis** (details in [docs/shading-rules.md](docs/shading-rules.md), Japanese)
  - Map simulator: redraws the map from the block positions alone and compares it pixel by pixel with the planned
    colours (tested to match in every staircase, support and dithering mode).
  - Shading & build check: pixels that differ from the plan, pixels whose shade depends on the terrain (empty column to
    the north), blocks that would fall or break, dark / normal / light counts, the tallest column, steps and drops; the
    shading analysis view shows where they are.
  - Shading test pad: a one-map structure with steps of different sizes, stairs, zigzags and east/west height differences,
    plus the predicted map. Build it, make a map and compare to check Bedrock's rule without reading any world data.
- **Height optimisation**
  - With a maximum height (Valley), the best shading within that height is computed (per-column dynamic programming,
    optimal without dithering, with ordered or Knoll dithering; a per-pixel limit with error diffusion).
  - "Find the best height for this image" converts flat, with each limit and unlimited, measures the difference seen from a
    distance (linear-light blur + CIEDE2000) and recommends where the curve flattens. Example: a photo needing 37 blocks
    unlimited got 83 % of the improvement at 7 blocks.
  - "Use a background colour that stays flat" (pure white only exists in the light shade), "keep it low", scattered drop
    rows so flat areas get no dark lines, placement check (Y −64 … 319, recommended Y), height map.
- Support blocks: none / under blocks that need it / all (survival, optimised) / all (double); verified never to show on the map.
- Manual pixel painting in the 2D build view (undo, back to automatic, saved in the project).
- Bedrock specifics: grass and some leaves change map colour with the biome and are off by default.

**In-game calibration (read only)** — details in [docs/calibration.md](docs/calibration.md) (Japanese)
- Exports a shading test pad (steps −4 … +4 with full blocks, carpets and slabs, east/west steps, empty north column,
  biome tints) and a colour test pad with all 323 blocks; `pad.json` records every sample position.
- After the game has drawn a map of the pad, BMAP reads the map (`map_<ID>`, `colors`) from a **temporary copy** of the
  world database and compares it with the prediction. Worlds are never written. Map orientation, offsets and unexplored
  pixels are handled.
- From the brightness per step it derives the **smallest step that changes the shade**, the multipliers and whether
  shading is graded; the colour pad gives the real colour of each block. Results are saved as a colour profile marked
  "verified" and used by quantizing, height limits, layout and the 3D view.
- Every colour has a status: verified / API-derived / third-party / estimated / unknown. Estimates are never shown as verified.
- `mapcheck` checks that every pixel of any drawn map is "base colour × multiplier". This confirmed the multipliers
  180 / 220 / 255, the rounding (floor from the base colour) and 13 base colours on maps drawn by Bedrock.
- Measurements are scored against candidate models from other implementations (Java, step 2, gd-codes rounding, parity,
  PowerNukkitX). `refcompare` downloads gd-codes' 82 colour categories and compares them with BMAP. Findings:
  [docs/bedrock-research.md](docs/bedrock-research.md) (Japanese).

**Previews and guides**
- Predicted map colours, original, difference, shading analysis, 2D build view (textures, heights, chunk borders,
  coordinates), height map.
- **3D build view**: free orbit / pan / zoom, perspective (perspective-correct textures); texture / map colour / shade /
  height colouring; ambient occlusion and sun shadows; chunk and map lines; a height slice for building layer by layer;
  jump to the tallest column; hover highlight with coordinates and shade. 8 × 8 maps (5.4 M faces) are ready in about a second.
- Material list per map with stacks, alternatives, **owned and missing** counts and CSV export; build progress per map and chunk.
- Output: `.mcstructure` (64 × 64 / per map / whole) and a zip, placement instructions with `/structure load` commands,
  `placement.csv`, placement images, row-by-row steps, `materials.csv`, map preview PNG; behavior pack optional.

## Build and run

```bash
dotnet build BMAP.sln
```

The first build downloads the textures (about a minute, needs internet; skip with `-p:BundleTextures=false`).

```bash
dotnet run --project src/BMAP.App
```

```bash
dotnet test tests/BMAP.Tests
```

Release zip (single exe, no .NET needed; textures are offered on first start):

```bash
powershell -ExecutionPolicy Bypass -File tools/publish.ps1
```

Command line (`BMAP.Cli`): `build` (export), `analyze` (height analysis), `bench` (compare dithering and metrics),
`render` (3D view to PNG with timings per stage), `pad` / `calibrate` / `mapcheck` / `maps` / `worlds` (calibration,
read only), `textures`. Run without arguments for usage.

## Loading .mcstructure files

1. The files are in the `mcstructure` folder of the output (the placement text lists coordinates and commands).
2. In game: `/give @s structure_block`, place it, set it to Load, and Import the file.
3. Load it with the right offset, or run the `/structure load` command from the placement text after importing.

## Layout

| Path | Contents |
|---|---|
| `src/BMAP.Core` | Everything independent of the UI (see below) |
| `src/BMAP.App` | Avalonia GUI (English / Japanese) |
| `src/BMAP.Cli` | Command line |
| `tests/BMAP.Tests` | xUnit tests |
| `tools/PaletteGen` | Builds `palettes/bedrock-full-v2.json` from `blocks.txt` (validated against Mojang's metadata) |
| `tools/publish.ps1`, `tools/make-icon.ps1` | Release zip, app icon |
| `docs/` | [Shading rules](docs/shading-rules.md), [in-game calibration](docs/calibration.md), [other implementations checked](docs/bedrock-research.md), [benchmarks](docs/benchmarks.md) |

| Feature | Code (`src/BMAP.Core`) |
|---|---|
| Image processing | `Imaging/` |
| Colour spaces and distances | `ColorEngine/ColorMath.cs` |
| Blocks and colour sets | `Palette/`, `BlockEngine/PaletteSelection.cs` |
| Quantizing and dithering | `BlockEngine/ColorQuantizer.cs` (`.Knoll.cs`), `BlueNoise.cs` |
| Staircases and supports | `BlockEngine/BlockLayout.cs`, `ColumnIndex.cs` |
| Height analysis and placement | `BlockEngine/HeightAdvisor.cs` |
| Shading rule, map simulator, calibration | `Shading/` (`MapShading.cs`, `ShadingTestPad.cs`, `Calibration.cs`) |
| Reading Bedrock maps (read only) | `Bedrock/` (LevelDB, `level.dat`, `MapRecord`) |
| 2D / 3D previews | `Preview/LayoutRenderer.cs`, `Preview/Viewer3D.cs` |
| Export, guides, materials | `Export/`, `BlockEngine/BuildGuideGenerator.cs`, `Materials.cs` |
| Textures | `Textures/` |
| Projects | `Project/BmapProject.cs` (`*.bmap.json`) |

## Verification status

The game cannot be driven from the development environment, so these points are **not verified in game yet**:

| Item | Status |
|---|---|
| Shade multipliers 180 / 220 / 255 | **Verified** on 12 maps (~190 k pixels) drawn by Bedrock 1.21.132 – 1.26.51 |
| Base colours of the colour sets | **13 of 60 verified**; the rest are Java values (estimates) |
| Rounding | **Verified**: base × multiplier / 255, rounded down (gd-codes' rounding does not match) |
| Step rule | **Verified** (1.26.51): ±1 alternates by coordinate parity (+1 is light where x + z is odd, −1 dark where even), ±2 or more always. Used by default |
| Which block gives which colour | Not verified; measure with the colour test pad |
| Block identifiers and states | All 323 validated against Mojang's metadata (1.26.50); loading as structures not checked |

Logs are in `%LOCALAPPDATA%\BMAP\logs`.

## Known limitations

- APNG: first frame only. Video needs ffmpeg (on PATH or next to BMAP).
- Water (shade by depth) is not supported, as in MapartCraft.
- Writing map data directly into worlds was removed in v0.3.0 (too hard to get right). Worlds are only read (temporary copy).
- Empty columns appear to be drawn black on Bedrock (many opaque black pixels on real maps); the shade south of a transparent pixel depends on the terrain.
- Biome-tinted blocks (grass, some leaves) cannot be given a biome.
- The name of imported structures (`mystructure:…`) may differ between game versions.

## Projects and references

- [MapartCraft](https://github.com/rebane2001/mapartcraft) (rebane2001, GPL-3.0): colour set grouping, screen layout and
  many features. No MapartCraft code or images are included.
- [Cartographer](https://github.com/julienvincent/cartographer): a map art converter with several staircasing algorithms.
- [Image Map](https://github.com/tryashtar/image-map): the Bedrock map data format (`map_<ID>`, `colors`). No code used.
- [gd-codes/mc-pixelart-maker](https://github.com/gd-codes/mc-pixelart-maker) (MIT): Bedrock colour categories (downloaded at run time for comparison) and step-2 builds.
- [CloudburstMC/Nukkit](https://github.com/CloudburstMC/Nukkit), [PowerNukkitX](https://github.com/PowerNukkitX/PowerNukkitX): map shading formulas (re-implemented as candidate models; no code used).
- Minecraft docs: [BlockMapColorComponent](https://learn.microsoft.com/en-us/minecraft/creator/scriptapi/minecraft/server/blockmapcolorcomponent?view=minecraft-bedrock-stable),
  [Block.getMapColor (pre-release)](https://learn.microsoft.com/en-us/minecraft/creator/scriptapi/minecraft/server/block?view=minecraft-bedrock-experimental),
  considered for reading base colours (see docs/calibration.md).
- Pekka Väänänen, [Revisiting Yliluoma's ordered dither algorithm](https://30fps.net/pages/revisiting-yliluoma-2/):
  Knoll's pattern dithering, and why to dither in linear light.
- R. Ulichney, "The void-and-cluster method for dither array generation" (1993): blue noise.
- B. Ottosson, [Oklab](https://bottosson.github.io/posts/oklab/) (2020); G. Sharma et al., "The CIEDE2000 color-difference
  formula" (2005).
- S. Abasi et al., "Distance metrics for very large color differences" (2020, HyAB): tried, measured worse for this use, not adopted.

## License

[MIT License](LICENSE)

Minecraft is a trademark of Mojang Studios / Microsoft. This project is not affiliated with Mojang Studios or Microsoft.
Block textures are downloaded to the user's PC and are not part of this project.
