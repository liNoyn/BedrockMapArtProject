**DitherBench** compares Optimized, Perceptual and Hybrid using identical palettes, support options and placement rules.

Run from the repository root:

```powershell
dotnet run --project tools/DitherBench -c Release -- output 96 3 images
dotnet run --project tools/DitherBench -c Release -- output-128 128 3 images speed
dotnet run --project tools/DitherBench -c Release -- output-heights 96 1 images height
dotnet run --project tools/DitherBench -c Release -- output-detection 128 1 images detect
dotnet run --project tools/DitherBench -c Release -- output-sky 256 5 images sky
dotnet run --project tools/DitherBench -c Release -- output-all 48 3 images all
dotnet run --project tools/DitherBench -c Release -- output-all-heights 48 1 images all-height
dotnet run --project tools/DitherBench -c Release -- output-exact 8 1 images precision
dotnet run --project tools/DitherBench -c Release -- output-palette 48 3 images all-limited 2
dotnet run --project tools/DitherBench -c Release -- output-large-palette 256 3 images limited 6
dotnet run --project tools/DitherBench -c Release -- output-palette-heights 48 1 images all-height 6
```

`images` is optional and may contain PNG/JPEG reference photos. Image filenames become case names; use `astronaut.png`
and `coffee.png` from scikit-image to reproduce the 2026-10-24 review. Photos are resized with linear-light Lanczos3.
Ten deterministic synthetic images are included in the source, including similar-colour areas separated by outlines
with and without antialiasing. Nothing is downloaded by the tool.

Normal mode measures Flat, Classic, Valley and Valley with a height budget of 4. Each model is warmed up first;
reported time is the median of the requested number of conversion runs, including height planning. Model order rotates
across scenes and modes. Quality and layout analysis are outside the timed section.

`speed` uses gradient, sky, illustration, both outline scenes and the two named photos, Valley with/without budget 4.
`sky` uses only the sky scene with the same two height conditions.
`detect` measures only flat-area classification at 96/128/256/512 pixels, saving masks, interior coverage and median
classification time across five warm runs. Its size argument does not change this fixed resolution matrix.
`height` checks mixed, both outline scenes, astronaut and coffee at budgets 2/4/8/16, aligned/unaligned placement and Important/AllDouble
supports. It writes a diagnostic matrix and fails if a height or simulation check fails; it does not time the cases.
`all` runs the normal four terrain conditions for every `DitherMode`, including the legacy engines retained for saved
projects and the CLI. `all-height` extends the height matrix to every engine. With the two reference photos, these are
816 quality/speed cases and 1360 height/simulation cases respectively. Small images provide broad coverage; use 96/128
or larger comparisons before judging visual quality. More candidates or passes do not necessarily improve perceived
quality: report Pixel, Near, Far and Roughness together rather than choosing settings from Far alone.
`precision` diagnoses solid patches of every reachable colour in the selected built-in palette, in Flat and Valley,
across every engine. It reports changed pixels and quality measurements in `exact-colours.json`; the patch size is
fixed at 8x8 regardless of the size argument. It is a diagnostic report, while the corresponding precision tests
enforce exact-colour preservation (including feasible normal colours under a supported height limit).
The optional sixth argument is `MaxBlockTypes` (0 = unlimited). It applies to normal, height and limited modes;
the JSON/CSV rows record both the requested palette budget and the number of used entries. `limited` compares the
three primary engines on gradient, mixed, illustration, outline-aa, coffee (if provided) and rare-outline, using Flat
and Valley with height budget 4. `all-limited` extends this to all 17 engines: 204 cases with coffee available.
Rare-outline contains two similar light palette colours and a thin dark stripe; it tests how a palette budget treats
near-duplicate background colours and rare contrast. It uses the full selected palette to create the source even
when a budget is requested. Score individual cases as well as averages; a palette planning proxy can improve while
the actual dithered result becomes worse. Timings include palette planning, rendered alternatives and height search.

Outputs include JSON, CSV, source/preview PNGs and environment metadata. Roughness is 100 times the RMS spatial
difference of the Oklab quantization residual across horizontal/vertical visible neighbours. It measures introduced
local variation, separately from the DeltaE2000 accuracy scores; smaller is calmer, not necessarily more accurate.

For material budgets, Optimized and Hybrid prioritise source contours; Perceptual and the legacy engines retain
the average-colour policy. `Overall` remains the ordinary average error for every model. Rescore existing before/after
PNGs with the current model-specific policy as well (no conversion or timing is repeated):

```powershell
dotnet run --project tools/DitherBench -c Release -- compare-policy before-dir after-dir comparison.json
```

The comparison validates matching conditions and identical source pixels, then reports both average CIEDE2000 and
model-weighted CIEDE2000/Oklab scores, original timings and pixel equality. The distance column assumes the benchmark's
default Oklab matcher. Contour-priority results can have higher ordinary average error under a severe colour budget;
report both values. See `docs/model-priority-2026-10-24.md` for the weighting and measured tradeoff.

To benchmark an extracted source snapshot with the same harness, pass an absolute project path:

```powershell
dotnet run --project tools/DitherBench -c Release -p:CoreProject=C:/snapshot/src/BMAP.Core/BMAP.Core.csproj -- output-before 96 3 images
```

The snapshot must support the three model enum values and public APIs used by this tool. Measurements are descriptive,
not timing assertions for CI. See `docs/hybrid-review.md` for methodology and limitations.

The alpha scene deliberately contains holes. The simulator reports terrain-dependent pixels there and may count
colour mismatches for those pixels (it assumes a normal shade when the northern column is empty). Do not interpret
the total mismatch count as a failure on opaque pixels or claim exact in-game reproduction for transparent artwork.
