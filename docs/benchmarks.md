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
## Two-colour mixing (v0.10.0)

A user compared BMAP with MapartCraft on the same picture (`score <source> <map.png>...` measures finished 128 px
map images: per pixel, near = blurred σ 0.8 px, far = blurred σ 2 px, CIEDE2000). BMAP's earlier optimiser scored best
(near 2.27, far 0.98 vs MapartCraft 4.89 / 3.69) but looked worse: it built pale lavender from white, strong blue, pink
and yellow dots — the right average, rough up close. Restricting each pixel to the best **pair** (closest blend in linear
light + 0.1 × Oklab contrast of the pair × 2√(t(1−t))) plus its nearest colour fixed that:

| image | setting | px | near | far |
|---|---|---:|---:|---:|
| user's illustration | MapartCraft | 9.47 | 4.89 | 3.69 |
| | BMAP v0.9 optimiser | 9.21 | 2.27 | 0.98 |
| | **BMAP pairs** | **8.01** | 2.91 | 1.83 |
| anime (pale pink) | Floyd–Steinberg | 9.78 | 5.04 | 3.85 |
| | **BMAP pairs** | 9.36 | **3.63** | **2.66** |
| landscape | Floyd–Steinberg | 6.71 | 1.73 | 0.65 |
| | **BMAP pairs** | 6.47 | 2.12 | 1.05 |

- A plain Oklab distance limit on the candidates (tried first) made the result calm but lost hues the palette can only
  mix from far colours (pale pink hair turned beige), so it was dropped in favour of pairs.
- Contrast penalties 0.07–0.15 were close; 0.25 and above lost colour (far error ×2–3). The strength slider raises it.
- Speed: one map 0.2 s, 4 × 4 maps 1.2 s.
## Larger corpus, triples, hue-aware mixes (v0.11.0)

Corpus: 21 illustrations (the user's test set), 8 Kodak photos, 8 flat pale colours the palette lacks; one map each,
centre-cropped, Valley. `speckle` = extra local contrast the map adds over the source (Oklab × 100, neighbouring
pixels): the roughness people see up close, which blur-based scores reward instead of punishing.

| setting | group | px | near | far | speckle |
|---|---|---:|---:|---:|---:|
| Floyd–Steinberg | illustrations | 8.23 | 3.62 | 2.57 | 3.84 |
| v0.10 (pairs) | illustrations | 7.76 | 3.19 | 2.32 | 4.32 |
| **v0.11** | illustrations | **7.45** | 3.58 | 2.50 | **3.54** |
| Floyd–Steinberg | photos | 7.94 | 2.10 | 0.97 | 3.37 |
| v0.10 (pairs) | photos | 7.70 | 2.47 | 1.38 | 3.03 |
| **v0.11** | photos | **7.31** | 2.72 | 1.38 | **2.61** |
| v0.10 (pairs) | flat pale colours | 14.53 | 7.11 | 6.70 | 8.78 |
| **v0.11** | flat pale colours | 13.03 | **5.77** | **5.02** | 8.76 |

What changed and why:
- **Triples**: colours inside a triangle of map colours can be matched exactly; at equal speckle the far error dropped.
- **Pair partners from all colours** (12 closest × any partner) and the best partners in the triple search: a pale
  pink needs pink wool, which is nowhere near it in linear light.
- **Off-hue penalty** (0.5 × distance from the target's hue line): salmon orange was mixed from pure red (redstone
  block) and yellow — right on average, loud dots. A penalty on per-pixel distance instead fixed that but turned pale
  pink hair grey, so it was replaced by the hue-line distance (white and pink lie on the pink hue line).
- **Quadratic contrast penalty** (0.05 × spread²): white with emerald for a pale green is now avoided; 0.2+ flattened
  pale colours too much.
- Blur σ 1.5 instead of 1.0 in the optimiser: less speckle at the same far error.
- Very pale tints (ΔE ≈ 10 from white, e.g. #F6DFDE) still become white/grey: any mix is rougher than the tint.
- Photos: Floyd–Steinberg stays closer from a distance; v0.11 is calmer pixel by pixel.

Block selection: "Recommended (no support, all colours)" — the first block per colour set that needs no support and
does not fall — gives the same results as "one block per colour" on the whole corpus (illustrations identical, photos
within 0.04) with fewer support blocks; only the grass colour (slime) still needs support. It is now the first preset.
The survival preset lacks colours and is worse (illustrations far 2.84 vs 2.50).
## Patches in pale areas at 2 × 2 maps (v0.11.1)

A user saw the pink hair break into square patches of grey and pink at 2 × 2 maps. Cause: v0.11's off-hue penalty and
quadratic contrast penalty put "grey alone" and "white + pink" at a near-tie in pale pinks, so small shading
differences and the 8 × 8 block noise of compressed frames flipped whole areas between the two plans. (Adding the
colours of near-best plans or plans of a blurred copy did not help: it brought red dots back into salmon orange.)

Fix:
- The off-hue penalty starts beyond a tolerance of 0.05 (Oklab): pink wool slightly off a pale pink's hue is fine.
- The quadratic contrast penalty only counts contrast ACROSS the target's hue (spread of signed off-hue distances):
  red + yellow for salmon loses as before, white + pink for pale pink is no longer touched.
- The optimiser works towards a bilateral-filtered copy (5 × 5, range σ 4): block noise no longer decides pixel by
  pixel. The diffusion start uses the image itself, so shades under a height limit are unchanged.

Test `Optimized_HasNoPatchesInAPaleGradientWithBlockNoise` (flat pale pinks with block noise) fails with v0.11's
settings (pink share varies by 0.23 between tiles) and passes now. Corpus, one map: illustrations far 2.44 → 2.29,
speckle 3.60 → 3.80 (more of the pale hues kept); photos far 1.40 → 1.34.
## Palette measured in game (v0.12.0)

After the colour pad measurement (docs/calibration.md) the palette has all 79 colour sets verified, 351 blocks, and the
16 wool slab colours on by default; the optimiser's per-pixel weights were lowered (0.05 / 0.1) because the mix plans
already keep colours calm. Corpus, one map each, Valley:

| setting | group | px | near | far | speckle |
|---|---|---:|---:|---:|---:|
| Floyd–Steinberg | illustrations | 8.07 | 3.44 | 2.39 | 3.43 |
| v0.11.1 | illustrations | 7.50 | 3.35 | 2.28 | 3.80 |
| **v0.12.0** | illustrations | 7.82 | **2.95** | **1.70** | 4.19 |
| Floyd–Steinberg | photos | 7.69 | 1.98 | 0.87 | 3.27 |
| v0.11.1 | photos | 7.44 | 2.69 | 1.34 | 2.58 |
| **v0.12.0** | photos | 7.30 | 2.35 | 0.98 | 2.82 |
| v0.11.1 | flat pale colours | 13.38 | 6.38 | 5.62 | 8.70 |
| **v0.12.0** | flat pale colours | 14.75 | **3.32** | **1.80** | 11.97 |

The 2 x 2 hair that broke into patches in v0.11 stays even. Known limitation: very pale pinks right at the tie between
"grey alone" and "white + pink" (#F2D4D7–#F1D1D5) still turn grey with stripes under block noise (docs/TODO.md).
## Random search over the optimiser (v0.13.0)

A random search over the optimiser's settings on the whole corpus (score = near + far + 0.5 x speckle; illustrations
count twice) picked a balanced point: lower per-pixel weights (0.03 / 0.05), a wider far kernel (σ 3, weight 2),
σ1 1.25, softer plan penalties (hue 0.4, quad 0.5, tolerance 0.03) and two passes of neighbour swaps. The most
accurate point found (pw 0.02) was a little closer still but visibly grainier. Corpus, one map each, Valley:

| setting | group | px | near | far | speckle |
|---|---|---:|---:|---:|---:|
| Floyd–Steinberg | illustrations | 8.07 | 3.44 | 2.39 | 3.43 |
| v0.12.0 (+ swaps) | illustrations | 7.80 | 2.92 | 1.69 | 4.15 |
| **v0.13.0** | illustrations | 8.12 | **2.58** | **1.26** | 4.90 |
| Floyd–Steinberg | photos | 7.65 | 1.87 | 0.77 | 3.44 |
| v0.12.0 (+ swaps) | photos | 7.38 | 2.24 | 0.88 | 2.94 |
| **v0.13.0** | photos | 7.67 | 1.98 | **0.67** | 3.43 |
| v0.12.0 (+ swaps) | flat pale colours | 14.75 | 3.18 | 1.78 | 12.00 |
| **v0.13.0** | flat pale colours | 15.05 | **2.70** | **1.35** | 12.82 |

2 x 2 (illustration 1): near 3.44 → 2.51, far 1.88 → 0.98, and fewer grey patches in the pale pink hair. The trade is
a little more fine grain up close (speckle +0.7). Salmon (#F47B4D) stays orange + pink without red dots.

## Pale pinks no longer turn grey (v0.13.1)

A user's 2 x 2 anime frame drew the top of pale pink hair (#EED0D4) as plain grey #DCD9D3. Cause: the plan cost was
distance + contrast + off-hue; grey has the same lightness, so its distance (0.025, all of it lost chroma) beat
white + pink (0.011 + 0.018 for their contrast), and nothing charged grey for losing the colour (it sits on the hue
line, so the off-hue term was zero). New term PlanChroma (1): chroma lost along the target's hue beyond half of the
target's chroma. Pale pinks #EED0D4 / #ECD4D4 / #EDD1D6 now mix white + pink + grey; the old tie band #F2D4D7–#F1D1D5
passes the patch test too. Corpus: illustrations near 2.58 → 2.54, far 1.26 → 1.19; photos and flat colours unchanged;
the 2 x 2 frame far 1.26 → 0.84. Flat builds keep grey: without the bright shades the mix would be much darker, and a
stronger penalty only adds red dots.

## Speed (v0.13.2)

Same output bit for bit (hashes of 21 conversions: 1, 2 and 4 maps square; Valley, Flat, height limit 8, FS), faster:
the optimiser's error spreading runs on SIMD rows, the nearest-colour lists that the mix plans replace are no longer
built, the bilateral filter uses a weight table, and the nearest-colour search has a fast path for Oklab. Optimized,
4 x 4 maps (512 x 512): 1.28–1.67 s → 0.47–0.59 s.
