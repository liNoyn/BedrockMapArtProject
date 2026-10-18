# Benchmarks

How the conversion methods of Bedrock Map Art Project compare, and which defaults they led to.
Reproduce with:

```bash
dotnet run --project src/BMAP.Cli -c Release -- bench <image>... [--maps 1x1]
```

## Method

- 8 test images (6 landscape photos, 1 illustration, 1 logo), each converted to one map (128 × 128) with all
  60 map colours ("one block per colour" preset).
- **linΔE2000** (main measure): both the source and the predicted map are blurred in **linear light** (5×5 binomial)
  and compared with **CIEDE2000**. Light from neighbouring pixels adds up linearly in the eye, so this is how different
  the map looks from a normal viewing distance. It is independent of the metric the quantizer uses.
- **blurΔE76** (older measure, kept for reference): 3×3 blur on sRGB values, then CIE76. Blurring sRGB values is
  physically wrong (it is not how light mixes), and it favours dithering in sRGB.
- Lower is better. Times are per map on one desktop CPU (Release build).

## Results (mean of 8 images)

| Configuration | blurΔE76 | **linΔE2000** | ms |
|---|---:|---:|---:|
| Flat, no dither | 9.10 | 6.87 | 2 |
| Flat, Floyd–Steinberg (sRGB, before) | 5.28 | 3.85 | 7 |
| Flat, Floyd–Steinberg (linear light) | 6.02 | **3.55** | 7 |
| Flat, Sierra Lite (linear light) | 5.96 | **3.47** | 7 |
| Flat, Bayer 8×8 | 8.03 | 6.14 | 1 |
| Flat, blue noise | 8.06 | 6.14 | 14 |
| Flat, Knoll pattern (Bayer 8×8, linear) | 7.01 | **3.99** | 25 |
| Valley, no dither | 6.11 | 4.83 | 3 |
| Valley, Floyd–Steinberg (sRGB, before) | 2.57 | 1.91 | 21 |
| Valley, Floyd–Steinberg (linear light) | 3.16 | **1.59** | 18 |
| Valley, Sierra Lite (linear light) | 3.13 | **1.54** | 14 |
| Valley, Bayer 8×8 | 5.51 | 4.42 | 2 |
| Valley, Knoll pattern (Bayer 8×8, linear) | 4.68 | **2.62** | 42 |
| Valley ≤ 4 blocks, Floyd–Steinberg | 4.89 | 2.68 | 12 |
| Valley ≤ 4 blocks, Bayer 8×8 | 6.41 | 4.88 | 4 |
| Valley ≤ 4 blocks, Knoll pattern | 6.19 | **4.37** | 67 |

## Decisions

| Change | Result | Decision |
|---|---|---|
| Error diffusion in **linear light** instead of sRGB values | −8 % (flat) to −16 % (Valley) linΔE2000 | **Default on** |
| **Knoll pattern dithering** (Thomas Knoll's method used in Photoshop) instead of adding noise to the colour (Bayer) | −35 % (flat), −41 % (Valley), −10 % (height ≤ 4) | **Added**, recommended ordered dither |
| **Blue-noise** threshold matrix (void-and-cluster, Ulichney 1993) | same numbers as Bayer; no cross-hatch pattern | Added as an option (and for Knoll) |
| **Error bound**: overshoot of the diffused value limited to ±16 (instead of exact clamping or no bound) | Valley FS 1.607 (clamp) / 1.690 (no bound) → **1.594**; also keeps the brightness of dark areas | **Adopted** |
| **HyAB** distance in Oklab (Abasi et al., for large colour differences) | worse on both measures (e.g. Valley FS 1.61 → 2.07, measured before the error-bound change) | **Rejected** |
| "Plan shades with Viterbi, then dither inside the plan" for height-limited error diffusion | worse at every limit (e.g. limit 4: 3.44 → 3.59 blurΔE76) | **Rejected** (v0.3.0) |
| Height analysis measure | now linΔE2000 | **Changed** |

## Speed (8 × 8 maps, 1024 × 1024 blocks, Valley + Floyd–Steinberg, 5.4 M faces)

| Stage | Before | After |
|---|---:|---:|
| 3D mesh (faces, ambient occlusion, shadows) | 4 487 ms, ~260 MB occupancy grid | **1 038 ms**, compact column index |
| Shading analysis | 917 ms | **662 ms** |
| Quantize / layout / render | 834 / 350 / 628 ms | unchanged |

The mesh now computes shadows once per column (a "shadow height" marched over the column tops towards the sun)
instead of tracing a ray for every face, and stores occupancy per column instead of in a dense 3D grid.

## References

- Thomas Knoll's pattern dithering and Yliluoma's ordered dithering: Pekka Väänänen,
  [Revisiting Yliluoma's ordered dither algorithm](https://30fps.net/pages/revisiting-yliluoma-2/) (with comparisons).
- R. Ulichney, "The void-and-cluster method for dither array generation", SPIE 1913, 1993.
- B. Ottosson, [Oklab](https://bottosson.github.io/posts/oklab/) (2020).
- G. Sharma, W. Wu, E. Dalal, "The CIEDE2000 color-difference formula", Color Research & Application, 2005.
- S. Abasi, M. Amani Tehran, M. Fairchild, "Distance metrics for very large color differences", Color Research &
  Application, 2020 ([HyAB in the colour library](https://colour.readthedocs.io/en/latest/generated/colour.difference.delta_E_HyAB.html)).

## Optimised dithering (v0.9.0)

Measured with `convert <image> <out.png> --dither ...` on 7 images (an anime illustration with pale pink hair and skin,
which the palette lacks — the nearest map colour is ΔE2000 ≈ 10 away — plus the 6 panoramas), one map each, means:

| configuration | per-pixel ΔE2000 | linΔE2000 | ms |
|---|---:|---:|---:|
| Valley, Knoll Bayer (linear) | 8.13 | 2.38 | 233 |
| Valley, Floyd–Steinberg (linear) | 7.41 | 1.81 | 78 |
| **Valley, Optimised** | 7.69 | **1.08** | 192 |
| Flat, Knoll Bayer | 10.43 | 3.39 | 103 |
| Flat, Floyd–Steinberg | 9.38 | 2.95 | 44 |
| **Flat, Optimised** | 9.55 | **2.00** | 127 |
| Valley ≤ 4, Floyd–Steinberg | 8.09 | 2.18 | 77 |
| **Valley ≤ 4, Optimised** | 8.29 | **1.63** | 155 |

- The optimiser minimises `w0·|e|² + |G1 * e|² + |G2 * e|²` (e = map − source, G = Gaussians with σ 1 and 2) by
  single-pixel swaps among the 16 candidates closest to the source colour, in linear light, with errors in dark tones
  weighted up by (Y / 0.5)^-0.5. The per-pixel term w0 is the "dither strength" (0.05 at 100 %, 0.6 at 0 %).
- Working space: linear light beat Oklab and sRGB values (anime image: linΔE2000 2.24 vs 4.17 / 3.32 at the same w0).
  The dark weighting changed little (1.066–1.093), 0.5 kept for slightly lower per-pixel error.
- Starting from Floyd–Steinberg or Knoll gives the same result (2.24 / 2.25); 32 candidates instead of 16 gains 1 %
  at twice the time. 4 × 4 maps take 1.4 s.
- On the anime image the old default Knoll gave linΔE2000 3.88, Optimised 1.49: the hair stays pink instead of grey.