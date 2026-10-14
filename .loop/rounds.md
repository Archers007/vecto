# Fidelity loop — rounds log

Branch: `rick-fidelity-loop`. Input: `rick-input.png` (507×903 cartoon, never modified).
Metric (frozen for the whole loop): trace → render back at 1x → pixel-diff vs source.
MAE (mean abs error /255, lower better) primary; bad-pixel % (>12/255) secondary.
Scorer: `.loop/score.py` (release binary) for rounds 0–3; `.loop/score_harness.py`
(locally-built Core-only harness, validated byte-identical SVG + identical scores
vs the release binary) for rounds 4–8.

Baseline (release binary v0.4.3): best = `--detail high --max-colors 32` →
MAE=3.5969, PSNR=28.11dB, bad=10.40%, 96 paths.

## Round 1 — parameter search (max-colors, seeds, photo style)
| variant | MAE | bad% | notes |
|---|---|---|---|
| mc48 / mc64 | 3.6377 / 3.6363 | 10.49 / 10.47 | more colors ≠ better; adds noisy regions |
| mc24 | 3.6177 | 10.40 | close, still worse than mc32 |
| seed 2 / 7 | 3.6183 / 3.6252 | 10.44 / 10.49 | seed 1 (default) wins |
| photo style | 3.8336 | **9.88** | best bad% of any param run, but MAE much worse |

## Round 2 — more params (crisp+mc32, medium detail, polygon mode, seed 42, mc40)
All worse than baseline. Polygon mode much worse (MAE 4.95, 359KB). Medium detail 4.11.
Nothing beats 3.5969.

## Round 3 — bracket max-colors (28/36), photo/crisp × mc24
mc28 3.6121 closest. Parameter search declared plateaued; best stays
`--detail high --max-colors 32` @ MAE 3.5969.

**Reviewer verdict (round 3, vs baseline render):** NEEDS-WORK. Top artifacts:
1. Shirt hue shift mint-green → aqua-blue (systematic palette-merge error, largest area).
2. Teeth merging / lost separators at zoom.
3. Contour wobble on smile/chin lines.
Not seen: speckle, banding. Hair/coat/sunglasses/eyes faithful.

## Infra (between rounds 3 and 4)
- dotnet SDK 8.0 install: first attempt failed on disk-ownership (`tar` chown);
  worked after manual extract with --no-same-owner. Full SDK not needed for Cli
  (ImageSharp 4.1.3 build hangs); built a Core-only `LoopHarness` instead
  (.loop/LoopHarness, 0 warnings) replicating the CLI's trace → SVG → re-parse →
  render path exactly, incl. the `--check` planarity self-check.
- Harness validation: byte-identical SVG and MAE=3.5969 vs release binary. PASS.

## Round 4 — CODE: MinRegionArea (blended/high) 3 → 2 px
**MAE 3.5969 → 3.5770** (-0.0199). PSNR 28.23. Planarity PASS. Kept (d9c9248).

## Round 5 — CODE: MinRegionArea 2 → 1 px
MAE 3.5814 (+0.0044 vs r4 — worse), bad% 10.33, PSNR 28.30. Reverted; kept r4 state.

## Round 6 — CODE: MergeThreshold 0.055 → 0.04 (target: shirt hue shift)
MAE 3.7618 (**+0.18 worse**), bad% improved to 9.41%, but 152 paths (fragmentation
noise). Reverted. Lesson: keeping more palette colors adds noisy micro-regions
that hurt more than the hue fix helps, at least for MAE.

## Round 7 — CODE: FitToleranceSq (high) 0.09 → 0.04 px²
**MAE 3.5770 → 3.4054** (-0.1716, biggest single gain). PSNR 28.76, bad 10.11%.
Planarity PASS. Kept (1cb4cfd).

## Round 8 — CODE: FitToleranceSq 0.04 → 0.0225 px²
**MAE 3.4054 → 3.3422** (-0.0632). PSNR 28.92, bad **9.99%** (first sub-10%).
Planarity PASS. Kept (2c0a8c6). Round cap (8) reached — loop stops here.

## Final
- Start (best baseline): MAE 3.5969, PSNR 28.11dB, bad 10.40%, 96 paths.
- Final (tuned): **MAE 3.3422, PSNR 28.92dB, bad 9.99%, 91 paths.**
- Total gain: -0.2547 MAE (-7.1%), -0.41pp bad pixels.
- What helped: tighter curve-fit tolerance (two steps: -0.17, -0.06), smaller
  min region area (-0.02). What didn't: more palette colors, lower merge
  threshold, polygon mode, medium detail, other seeds/styles.
- Not a 100% match and never will be via tracing: ~10% of pixels still differ
  by >12/255, concentrated in fine-detail face areas (teeth, thin lines) and
  anti-aliased edges. The remaining error is dominated by color quantization
  (palette merges, e.g. shirt hue) which resists constant-tweaking.
- dotnet build: Vecto.Core + LoopHarness build clean (0 warnings). Vecto.Cli
  could NOT be built on this box (ImageSharp 4.1.3 restore hangs); the Core-only
  harness was validated as a faithful substitute.
- Branch `rick-fidelity-loop` has 3 local commits; NOT pushed (no auth from this box).
