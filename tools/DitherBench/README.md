**DitherBench** compares Optimized, Perceptual and Hybrid using identical palettes, support options and placement rules.

Run from the repository root:

```powershell
dotnet run --project tools/DitherBench -c Release -- output 96 3 images
dotnet run --project tools/DitherBench -c Release -- output-128 128 3 images speed
dotnet run --project tools/DitherBench -c Release -- output-heights 96 1 images height
```

`images` is optional and may contain PNG/JPEG reference photos. Image filenames become case names; use `astronaut.png`
and `coffee.png` from scikit-image to reproduce the 2026-10-24 review. Photos are resized with linear-light Lanczos3.
Eight deterministic synthetic images are included in the source. Nothing is downloaded by the tool.

Normal mode measures Flat, Classic, Valley and Valley with a height budget of 4. Each model is warmed up first;
reported time is the median of the requested number of conversion runs, including height planning. Model order rotates
across scenes and modes. Quality and layout analysis are outside the timed section.

`speed` uses gradient, illustration and the two named photos, Valley with/without budget 4.
`height` checks mixed, astronaut and coffee at budgets 2/4/8/16, aligned/unaligned placement and Important/AllDouble
supports. It writes a diagnostic matrix and fails if a height or simulation check fails; it does not time the cases.

Outputs include JSON, CSV, source/preview PNGs and environment metadata. Roughness is 100 times the RMS spatial
difference of the Oklab quantization residual across horizontal/vertical visible neighbours. It measures introduced
local variation, separately from the DeltaE2000 accuracy scores; smaller is calmer, not necessarily more accurate.

To benchmark an extracted source snapshot with the same harness, pass an absolute project path:

```powershell
dotnet run --project tools/DitherBench -c Release -p:CoreProject=C:/snapshot/src/BMAP.Core/BMAP.Core.csproj -- output-before 96 3 images
```

The snapshot must support the three model enum values and public APIs used by this tool. Measurements are descriptive,
not timing assertions for CI. See `docs/hybrid-review.md` for methodology and limitations.
