# Research — CAD / scanned-drawing mode (2026-10-12)

Source: Unio research task RESEARCH-CAD-1, Perplexity Pro / GLM 5.3 Thinking with web sources (71 s),
prompted by issue #1. Lead review: technically sound (Sauvola formula, width = 2 x distance
transform, LWPOLYLINE bulge = tan(theta/4), GPL/LGPL tools are design references only for MIT
Vecto). Research input, not an implementation. The 2026-10-12 code review revised Stage 1 — see
`docs/ai/reviews/2026-10-12-review.md` (VEC-07): thin-stroke survival (8->4 diagonal bridging)
and Sauvola thresholding come first; deskew and text separation wait for centerline.

---

# Research Proposal — CAD-Scan Mode for Vecto (draft for lead review)

**Summary:** Add a `--mode cad` pipeline that (1) preprocesses scans (deskew, Sauvola binarization, despeckle), (2) extracts centerlines via skeletonization instead of filled-region tracing, (3) regularizes the resulting chains into snapped lines/arcs, and (4) separates text as filled shapes. This addresses the reported "crooked lines / distorted text" complaint directly: CAD linework is thin, constant-width ink that the current filled-region pipeline traces twice (once per side of each stroke), amplifying scan noise into wobbly double outlines.

## Answers to the research questions

**1. Preprocessing.** Binarization quality dominates everything downstream. Global Otsu fails on uneven scan illumination; local adaptive methods fix this. Sauvola's method thresholds each pixel using local mean and standard deviation over a window: $$T = m \cdot (1 + k(\delta/R - 1))$$, with k typically 0.2–0.5 and R = 128. Sauvola inherits from Niblack but suppresses the black-noise problem; its known weakness on low-contrast thin strokes is why Wolf's variant (mean-deviation normalization) is the usual companion choice. Deskew matters because the whole point of CAD mode is orthogonal snapping: estimate skew via projection-profile peaks or Hough-dominant angle of long lines, then rotate. Despeckle (remove components under a few pixels) removes scanner "hairs" — exactly what commercial tools like Scan2CAD ship as a "Smooth" raster effect.

**2. Centerline extraction.** Three options, all MIT-safe to reimplement from published algorithms (see Q7):

- Zhang-Suen (ZS): fast, predictable, two sub-iterations; the standard default.
- Guo-Hall (GH): better endpoint/corner preservation, fewer broken diagonal corners.
- Medial axis from distance transform: O(pixels), and — critically — it retains local thickness at each skeleton point, giving free stroke-width estimates, whereas pure thinning discards width.

Comparative studies on engineering drawings find ZS and GH skeletons "approximately the same with small but important variations," with some algorithms producing spurs/hooks and others double diagonals. Recommendation: compute the Euclidean distance transform once, derive the medial axis with it (width per skeleton pixel = 2 × DT value), and apply a small spur-pruning pass (remove branches shorter than ~2× median stroke width) for junction/spur handling. Contour-pair midline methods (trace both boundaries of a stroke, match, emit midline) are what AutoTrace's `-centerline` mode conceptually does and what Inkscape uses for centerline tracing; they avoid skeleton artifacts but junction matching is harder. Recent ML approaches exist (coarse-to-fine curve networks, stroke-encoder models) but are out of scope for a dependency-free MIT engine.

**3. Geometric regularization.** Scan2CAD-style tools apply, per vectorization preset: corner sharpening to 90°, gap jumping to mend broken straight lines, endpoint corner snapping, snapping angled lines slightly off 0°/90° back to exactly 0°/90°, grid snapping, and vertex merging within a user-defined snap distance. Map this to Vecto's skeleton chains: split chains at high-curvature points (reuse the existing k=3/68° corner detector), fit each segment with total-least-squares for lines and Kasa for circles/arcs (both already in the engine), then run a regularization pass — snap near-horizontal/vertical to exact axes, snap line pairs within tolerance to parallel, merge collinear segments across junctions, and snap endpoints within a radius. RANSAC/Hough add robustness to outlier skeleton pixels but total-least-squares on a cleaned chain is usually sufficient and cheaper.

**4. Text.** Classic text/graphics separation uses connected-component analysis with size and aspect-ratio heuristics, following Fletcher & Kasturi: characters are small, roughly uniform, and cluster into aligned rows; linework components are large or elongated. Key detail: characters touching leader lines are common in real drawings, and segmentation schemes handle them via stroke-linking/merging of character boxes. Recommendation (OCR-free, matching Vecto's scope): classify components by bounding-box size relative to estimated stroke width, group same-size neighbors into text rows, and route those components to the existing **filled-region outline tracer** — outlined text stays legible; centerlining glyphs would destroy them. This is also what the existing pipeline already does well.

**5. Output.** Emit centerline geometry as SVG `<path>` with `stroke-width` (per-chain width from the distance transform), `stroke-linecap="round"`, `fill="none"` — the roadmap's "path + stroke weight + round caps" item. Filled outlines remain correct for text and any wide/hatched areas (AutoTrace-style centerline tools keep an "outline anything wider than a specified width" option for exactly this). For CAD users the real destination is DXF: `LINE`, `ARC`, `CIRCLE`, and `LWPOLYLINE` entities are the standard 2D geometry set DXF consumers expect. `LWPOLYLINE` encodes arcs via per-vertex *bulge* values (tangent of a quarter of the included arc angle; 0 = straight), which maps neatly from Vecto's line/arc chains. Stage it after SVG strokes work.

**6. Evaluation.** The established methodology (Yan et al. benchmark, used by follow-up work): collect ground-truth vector drawings, rasterize at 1024×1024 with randomly sampled brush style, width, and background texture/noise, then compare the re-traced vector output to the ground truth in the vector domain. Metrics in the literature: Chamfer distance and F-score for stroke similarity, plus stroke-count and relative total stroke-length difference (~1% relative length difference is state-of-the-art). A CAD-specific harness: render synthetic DXF-like drawings (lines at 0/90/45°, arcs, circles, dimension text), apply skew (±2°), Gaussian blur, and salt noise, re-trace, and measure (a) endpoint error of line segments, (b) angle deviation from ground-truth axis angles, (c) whether parallel/collinear relations survive, (d) primitive counts vs. ground truth, (e) text-component IoU.

**7. Licensing.** AutoTrace: program GPL-2+, library `libautotrace` LGPL-2.1+. Potrace: GPL, with a paid proprietary-license option, and it has **no** centerline mode. inkscape-centerline-trace: GPL-2. GPL is copyleft — derivatives must be distributed under GPL — so none of this code may be copied into the MIT engine. Use them strictly as behavioral/design references; implement Zhang-Suen, Guo-Hall, medial-axis, Sauvola, and the regularization passes from the published papers (all pre-1995 algorithms, described in open literature). Reference datasets/code that are permissively licensed: marked-lineart-vectorization (Apache-2.0) and virtual_sketching (Apache-2.0) for benchmark inspiration.

## Staged plan

**Stage 1 — Scan front-end + text separation (effort: M; ~30%).** Deskew (projection profile over ±5°), Sauvola binarization, despeckle, connected-component text/linework split. Linework still goes through the *existing* filled-region pipeline. Already fixes "text gets distorted" partially. Risk: Sauvola window/`k` tuning; low risk overall.

**Stage 2 — Centerline extraction (effort: L; ~35%).** Distance transform → medial axis → chain tracing → spur pruning → stroke-width per chain. Output as SVG strokes with round caps. Risk: junction artifacts, spurs (mitigated by pruning + width-aware cleanup); the highest-uncertainty stage.

**Stage 3 — CAD regularization (effort: M; ~20%).** Chain splitting at corners, TLS/Kasa refits, axis/parallel/collinear snapping, gap jumping, endpoint merging within tolerance. Fixes "crooked lines."

**Stage 4 — DXF export (effort: S; ~15%).** Map chains to LINE/ARC/LWPOLYLINE (bulge) entities.

Pseudocode for the core Stage-2/3 seam:

```
dt = distance_transform(binary)
skel = medial_axis(binary)                # width(px) = 2*dt[px]
prune_spurs(skel, max_len = 2 * median_width)
chains = trace_chains(skel, split_at = junctions | corners(k=3, 68deg))
for chain in chains:
    chain.width = median(2*dt over chain)
    if line_fit_TLS(chain).rms < eps:
        chain = line(chain.p0, chain.p1)
    elif arc_fit_Kasa(chain).rms < eps:
        chain = arc(chain.center, chain.r, chain.a0, chain.a1)
regularize(all_chains):
    snap_axis(0, 90, 45, tol = 1.5deg)    # Scan2CAD-style
    merge_collinear(tol = 0.5 * width)
    snap_endpoints(radius = 0.5 * width)
emit_svg_strokes(chains, cap="round", fill="none")
```

## Proposed validation (unverified, to be run by the implementation agent)

- Unit tests: synthetic bitmaps (axis-aligned rectangle strokes at known widths 2–5 px) asserting skeleton width error < 0.5 px and zero angle deviation after snapping.
- Regression: existing fixture set must produce byte-identical output with `--mode` unset.
- Smoke: one synthetic scan (skew 1.5°, Gaussian blur σ=1, 2% noise) traced end-to-end; assert line endpoints within 1 px of ground truth, primitive count within 10%.
- Lead review of the research doc itself, per the task's Validate gate; no repo changes in this batch.

**Missing context to confirm at implementation time:** exact API shape of Vecto's region/curve types (inferred from the task description only), acceptable Sauvola window size vs. target scan DPI, and whether the CLI flag surface should be one `--mode cad` or individual toggles.

Sources

- [PDF] A New Local Adaptive Thresholding Technique in Binarization - arXiv: https://arxiv.org/pdf/1201.5227.pdf
- [PDF] Adaptive thresholding methods for documents image binarization: https://scispace.com/pdf/adaptive-thresholding-methods-for-documents-image-49n1n3itqh.pdf
- Adaptive document image binarization - ScienceDirect.com: https://www.sciencedirect.com/science/article/abs/pii/S0031320399000552
- A Novel Adaptive Deskewing Algorithm for Document Images - MDPI: https://www.mdpi.com/1424-8220/22/20/7944
- Research on Deskew Algorithm of Scanned Image - ADS: https://ui.adsabs.harvard.edu/abs/2018icma.conf...76Z/abstract
- GitHub - Bidur-Khanal/De-skew-scanned-pdf: https://github.com/Bidur-Khanal/De-skew-scanned-pdf
- 10 Raster Effects to Optimize your Vector Conversion - Scan2CAD: https://www.scan2cad.com/blog/tips/raster-effects/
- Scan2CAD Regular - Cadvance: https://www.cadvance.com/scan2cad.htm
- Choosing a thinning method - CRAN: https://cran.r-project.org/web/packages/thinr/vignettes/choosing-a-method.html
- [PDF] COMPARISON OF THINNING ALGORITHMS FOR VECTORIZATION ...: http://www.jatit.org/volumes/Vol94No2/2Vol94No2.pdf
- GitHub - preslavsh/skeletonization: https://github.com/preslavsh/skeletonization
- GitHub - autotrace/autotrace: bitmap to vector graphics ...: https://github.com/autotrace/autotrace
- Tracing double Lines into Center Lines - LightBurn Software Forum: https://forum.lightburnsoftware.com/t/tracing-double-lines-into-center-lines/34828
- Line Drawing Vectorization via Coarse-to-Fine Curve Network ...: https://hongbofu.people.ust.hk/doc/Line%20Drawing%20Vectorization%20via%20Coarse-to-Fine%20Curve%20Network%20Optimization_CGF2023.pdf
- End-to-End Line Drawing Vectorization - Association for the ...: https://cdn.aaai.org/ojs/20379/20379-13-24392-1-2-20220628.pdf
- Vectorization Settings | Tips & Advice For Users - Scan2CAD: https://www.scan2cad.com/blog/tips/vectorization-settings-scan2cad/
- [PDF] Scan2CAD features: http://www.cnc.co.za/docs/scan2cad-features.pdf
- Mending Breaks in Raster Lines for Conversion | Guides: https://www.rastertovector.com/articles/mending-line-breaks/
- Text/GraphicsSeparationRevisited - Springer: https://link.springer.com/content/pdf/10.1007/3-540-45869-7_24.pdf
- Vector-based segmentation of text connected to graphics in ...: https://www.academia.edu/49405380/Vector_based_segmentation_of_text_connected_to_graphics_in_engineering_drawings
- Centerline Trace - SA International: https://support.thinksai.com/hc/en-us/articles/4404795059092-Centerline-Trace
- Import DXF (AutoCAD)—ArcGIS CityEngine Resources | Documentation: https://doc.arcgis.com/en/cityengine/2024.0/help/help-import-dxf.htm
- Common Entities | DXF Standards Explained: https://takuto-na.github.io/dxf-standards-explained/geometry/common-entities.html
- Extracting LWPOLYLINE Vertices with ezdxf · CAD/GIS/BIM: https://www.cad-gis-bim-interop.org/python-parsing-geometry-extraction/ezdxf-deep-dive/extracting-lwpolyline-vertices-with-ezdxf/
- LWPOLYLINE (DXF) - Autodesk Knowledge Network: https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-748FC305-F3F2-4F74-825A-61F04D757A50.htm
- Stroke Vectorization via Involution Prediction - igl.ethz.ch: https://igl.ethz.ch/projects/involution-stroke-vectorization/stroke-vectorization-via-involution-prediction-vmv-2026-gerstner-et-al.pdf
- Single‐Line Drawing Vectorization - Magne - 2025 - Computer ...: https://onlinelibrary.wiley.com/doi/10.1111/cgf.70228
- Potrace: https://potrace.sourceforge.net/
- GitHub - fablabnbg/inkscape-centerline-trace: A bitmap ...: https://github.com/fablabnbg/inkscape-centerline-trace
- GNU General Public License - Wikipedia: https://en.wikipedia.org/wiki/GNU_General_Public_License
- GNU General Public License (GPL) 2.0 | Interoperable Europe Portal: https://interoperable-europe.ec.europa.eu/licence/gnu-general-public-license-gpl-20
- GitHub - nopperl/marked-lineart-vectorization: Vectorization ...: https://github.com/nopperl/marked-lineart-vectorization
- GitHub - MarkMoHR/virtual_sketching: [SIGGRAPH 2021] General ...: https://github.com/MarkMoHR/virtual_sketching
- [PDF] A Sequential Thinning Algorithm For Multi - arXiv: https://arxiv.org/pdf/1710.03025.pdf
- [PDF] An Improved Sauvola Approach on Document Images Binarization: https://jtec.utem.edu.my/jtec/article/view/2548/2826
- [PDF] Adaptive document image binarization - Semantic Scholar: https://www.semanticscholar.org/paper/Adaptive-document-image-binarization-Sauvola-Pietik%C3%A4inen/be97923dbcdaf8b1496b637ed156656d8874f552
- [PDF] A Review on an Efficient Iterative Thinning Algorithm - ijirset: https://www.ijirset.com/upload/2017/icmtest/79_Thinning%20Algorithm.pdf
- The GNU General Public License v3.0 - Free Software Foundation: https://www.gnu.org/licenses/gpl-3.0.en.html
- potrace/LICENSE at master - GitHub: https://github.com/oslllo/potrace/blob/master/LICENSE
- Adaptive cropping and deskewing of scanned documents based on ...: https://dl.acm.org/doi/10.1007/s00371-020-01952-z
- Component Segmentation of Engineering Drawings Using Graph ...: https://arxiv.org/pdf/2212.00290.pdf
- Import DXF (Autocad)—ArcGIS CityEngine Resources | Documentation: https://doc.arcgis.com/en/cityengine/2021.1/help/help-import-dxf.htm
- LWPolyline — ezdxf 1.4.4 documentation - Read the Docs: https://ezdxf.readthedocs.io/en/stable/dxfentities/lwpolyline.html
- LWPOLYLINE | AutoCAD DXF Format Documentation: https://documentation.help/AutoCAD-DXF/WS1a9193826455f5ff18cb41610ec0a2e719-79fc.htm
- Deskew PDF - Straighten Tilted Scan Pages - OxygenPDF: https://oxygenpdf.com/tools/deskew-pdf
