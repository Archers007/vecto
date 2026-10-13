using System.Runtime.CompilerServices;

namespace Vecto.Core;

/// <summary>
/// Per-chain geometry passes that run between boundary extraction and curve fitting.
/// Everything here operates on one chain at a time, so results stay identical for the
/// two regions that share the chain.
/// </summary>
internal static class ChainGeometry
{
    /// <summary>
    /// Clamped Laplacian smoothing: flattens the staircase into sub-pixel positions while
    /// never drifting more than <paramref name="clamp"/> px from the raster boundary.
    /// Chain endpoints and detected corners stay pinned to their exact lattice points.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void SmoothInPlace(List<Vec2> pts, bool closed, IReadOnlyList<int> pinned, int iterations, double lambda, double clamp)
    {
        int n = closed ? pts.Count - 1 : pts.Count;
        if (n < 3) return;
        var orig = new Vec2[n];
        for (int i = 0; i < n; i++) orig[i] = pts[i];
        var cur = (Vec2[])orig.Clone();
        var next = new Vec2[n];
        var pin = new bool[n];
        var lam = new double[n];
        Array.Fill(lam, lambda);
        foreach (var i in pinned)
            if (i >= 0 && i < n) pin[i] = true;
        if (!closed)
        {
            pin[0] = true;
            pin[n - 1] = true;
        }
        // damp smoothing next to pinned corners: full-strength Laplacian bulges the
        // flanks of sharp tips (thin features fatten into lobes)
        for (int i = 0; i < n; i++)
        {
            if (!pin[i]) continue;
            for (int d = 1; d <= 2; d++)
            {
                double factor = d == 1 ? 0.25 : 0.55;
                foreach (var j in new[] { i - d, i + d })
                {
                    int idx = closed ? ((j % n) + n) % n : j;
                    if (idx >= 0 && idx < n) lam[idx] = Math.Min(lam[idx], lambda * factor);
                }
            }
        }
        double clampSq = clamp * clamp;
        for (int it = 0; it < iterations; it++)
        {
            for (int i = 0; i < n; i++)
            {
                if (pin[i])
                {
                    next[i] = cur[i];
                    continue;
                }
                var prev = cur[i == 0 ? n - 1 : i - 1];
                var nxt = cur[i == n - 1 ? 0 : i + 1];
                var target = new Vec2((prev.X + nxt.X) * 0.5, (prev.Y + nxt.Y) * 0.5);
                var p = cur[i] + (target - cur[i]) * lam[i];
                var drift = p - orig[i];
                if (drift.LengthSq > clampSq) p = orig[i] + drift.Normalized() * clamp;
                next[i] = p;
            }
            (cur, next) = (next, cur);
        }
        for (int i = 0; i < n; i++) pts[i] = cur[i];
        if (closed) pts[n] = pts[0];
    }

    /// <summary>
    /// Sub-pixel edge refinement: slides each boundary point along its normal to where the
    /// source image's coverage crosses 50% between the two region colors. Anti-aliasing
    /// encodes the true edge position at sub-pixel precision — the crack lattice discards
    /// it, this recovers it. Corners/junctions stay pinned; per-chain, so planarity holds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void SubpixelRefine(List<Vec2> pts, bool closed, IReadOnlyList<int> pinned,
        RasterImage img, Rgba32? left, Rgba32? right, double maxShift)
    {
        if (left == null && right == null) return;
        int n = closed ? pts.Count - 1 : pts.Count;
        if (n < 3) return;
        var pin = new bool[n];
        foreach (var i in pinned)
            if (i >= 0 && i < n) pin[i] = true;
        if (!closed)
        {
            pin[0] = true;
            pin[n - 1] = true;
        }

        double axisR = 0, axisG = 0, axisB = 0, axisLenSq = 0;
        if (left is { } l && right is { } r)
        {
            axisR = l.R - r.R;
            axisG = l.G - r.G;
            axisB = l.B - r.B;
            axisLenSq = axisR * axisR + axisG * axisG + axisB * axisB;
            if (axisLenSq < 1) return;   // indistinguishable colors — nothing to measure
        }

        var result = new Vec2[n];
        for (int i = 0; i < n; i++)
        {
            var p = pts[i];
            result[i] = p;
            if (pin[i]) continue;
            var prev = pts[i == 0 ? (closed ? n - 1 : 0) : i - 1];
            var nxt = pts[i == n - 1 ? (closed ? 0 : n - 1) : i + 1];
            var tangent = (nxt - prev).Normalized();
            if (tangent.LengthSq < 0.5) continue;
            var normal = new Vec2(tangent.Y, -tangent.X);   // toward the LEFT region (y-down)

            double f0 = CoverLeft(p - normal);
            double f1 = CoverLeft(p);
            double f2 = CoverLeft(p + normal);
            double t;
            if ((f0 - 0.5) * (f1 - 0.5) <= 0 && Math.Abs(f1 - f0) > 1e-9)
                t = -1 + (0.5 - f0) / (f1 - f0);
            else if ((f1 - 0.5) * (f2 - 0.5) <= 0 && Math.Abs(f2 - f1) > 1e-9)
                t = (0.5 - f1) / (f2 - f1);
            else
                continue;
            if (double.IsNaN(t)) continue;
            result[i] = p + normal * Math.Clamp(t, -maxShift, maxShift);
        }
        for (int i = 0; i < n; i++) pts[i] = result[i];
        if (closed) pts[n] = pts[0];

        double CoverLeft(Vec2 q)
        {
            var (sr, sg, sb, sa) = Sample(img, q.X, q.Y);
            if (left == null) return 1 - sa;   // left side is transparency
            if (right == null) return sa;
            var rr = right.Value;
            double dot = (sr - rr.R) * axisR + (sg - rr.G) * axisG + (sb - rr.B) * axisB;
            return Math.Clamp(dot / axisLenSq, 0, 1);
        }
    }

    /// <summary>
    /// Fraction of the left region's colour in the source at <paramref name="q"/> (bilinear),
    /// projected on the left↔right colour axis; NaN when the two colours are indistinguishable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double CoverLeft(RasterImage img, Rgba32? left, Rgba32? right, Vec2 q)
    {
        var (sr, sg, sb, sa) = Sample(img, q.X, q.Y);
        if (left == null) return 1 - sa;
        if (right == null) return sa;
        var (l, r) = (left.Value, right.Value);
        double ar = l.R - r.R, ag = l.G - r.G, ab = l.B - r.B;
        double lenSq = ar * ar + ag * ag + ab * ab;
        if (lenSq < 1) return double.NaN;
        return Math.Clamp(((sr - r.R) * ar + (sg - r.G) * ag + (sb - r.B) * ab) / lenSq, 0, 1);
    }

    /// <summary>
    /// Pixel (<paramref name="x"/>, <paramref name="y"/>)'s left-region coverage on the left↔right colour
    /// axis; NaN outside the image, for indistinguishable colours, or when the pixel's colour lies
    /// well off that axis (a third colour, not a blend of the two).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double PixelCoverLeft(RasterImage img, Rgba32? left, Rgba32? right, int x, int y)
    {
        if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) return double.NaN;
        int pi = (y * img.Width + x) * 4;
        double sa = img.Pixels[pi + 3] / 255.0;
        if (left == null) return 1 - sa;
        if (right == null) return sa;
        var (l, r) = (left.Value, right.Value);
        double ar = l.R - r.R, ag = l.G - r.G, ab = l.B - r.B;
        double lenSq = ar * ar + ag * ag + ab * ab;
        if (lenSq < 1) return double.NaN;
        double dr = img.Pixels[pi] - r.R, dg = img.Pixels[pi + 1] - r.G, db = img.Pixels[pi + 2] - r.B;
        double f = Math.Clamp((dr * ar + dg * ag + db * ab) / lenSq, 0, 1);
        double er = dr - f * ar, eg = dg - f * ag, eb = db - f * ab;
        return er * er + eg * eg + eb * eb > 0.0625 * lenSq ? double.NaN : f;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static (double R, double G, double B, double A) Sample(RasterImage img, double x, double y)
    {
        double u = x - 0.5, v = y - 0.5;   // pixel centers sit at +0.5 in crack coordinates
        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
        double fx = u - x0, fy = v - y0;
        double sr = 0, sg = 0, sb = 0, sa = 0;
        for (int dy = 0; dy <= 1; dy++)
        {
            for (int dx = 0; dx <= 1; dx++)
            {
                int xi = Math.Clamp(x0 + dx, 0, img.Width - 1);
                int yi = Math.Clamp(y0 + dy, 0, img.Height - 1);
                double w = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy);
                int pi = (yi * img.Width + xi) * 4;
                sr += w * img.Pixels[pi];
                sg += w * img.Pixels[pi + 1];
                sb += w * img.Pixels[pi + 2];
                sa += w * img.Pixels[pi + 3];
            }
        }
        return (sr, sg, sb, sa / 255.0);
    }

    /// <summary>Runs between two pins shorter than this are a thin feature's end (see CornerPolygon.Validate).</summary>
    public const int ShortRun = 6;

    /// <summary>
    /// Sub-pixel corner relocation: each surviving corner moves from its lattice pin to the
    /// intersection of the two flank lines fitted to the refined geometry on either side.
    /// Flank directions within <paramref name="snapDeg"/> of a 45° multiple snap exactly —
    /// and both corners of a shared straight side fit the identical line, so snapped sides
    /// come out mathematically straight. A short side between two corners (a thin bar's end) runs
    /// square to its long neighbour instead of trusting its few AA-bent points. Chain-local, so
    /// planarity holds. Junctions (chain endpoints) stay pinned: consistent relocation there
    /// needs a multi-chain solve (ROADMAP).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void RelocateCorners(List<Vec2> pts, bool closed, IReadOnlyList<int> corners, double maxShift, double snapDeg)
    {
        int n = closed ? pts.Count - 1 : pts.Count;
        if (n < 7 || corners.Count == 0) return;
        var pins = new SortedSet<int>(corners);
        if (!closed)
        {
            pins.Add(0);
            pins.Add(n - 1);
        }

        var moved = new List<(int Index, Vec2 P)>(corners.Count);
        foreach (var c in corners)
        {
            // the short end of a thin bar (fewer than ShortRun points) has too few, AA-bent
            // points for a line of its own: it runs square to the long flank instead
            bool shortL = Run(c, -1).Count < ShortRun, shortR = Run(c, +1).Count < ShortRun;
            var left = shortL && !shortR ? null : FlankLine(c, -1);
            var right = shortR && !shortL ? null : FlankLine(c, +1);
            if (left == null && right is { } rf) left = ShortFlank(c, -1, rf);
            if (right == null && left is { } lf) right = ShortFlank(c, +1, lf);
            if (left is not { } l || right is not { } r) continue;
            double cross = l.Dir.X * r.Dir.Y - l.Dir.Y * r.Dir.X;
            if (Math.Abs(cross) < 0.2) continue;   // near-parallel flanks — not a trustworthy corner
            double t = ((r.Pt.X - l.Pt.X) * r.Dir.Y - (r.Pt.Y - l.Pt.Y) * r.Dir.X) / cross;
            var q = new Vec2(l.Pt.X + l.Dir.X * t, l.Pt.Y + l.Dir.Y * t);
            if (q.DistSq(pts[c]) > maxShift * maxShift) continue;   // implausible jump — keep the pin
            moved.Add((c, q));
            // the shoulders' own 50%-crossings are bent by the transverse edge's AA this
            // close to a corner — project them onto the flank lines they were excluded from
            Shoulder(c, -1, l);
            Shoulder(c, +1, r);
        }
        foreach (var (i, p) in moved) pts[i] = p;
        if (closed) pts[n] = pts[0];

        void Shoulder(int c, int dir, (Vec2 Pt, Vec2 Dir) line)
        {
            int j = c + dir;
            int w = closed ? ((j % n) + n) % n : j;
            if (w < 0 || w >= n || pins.Contains(w)) return;
            var p = pts[w];
            var proj = line.Pt + line.Dir * (p - line.Pt).Dot(line.Dir);
            if (proj.DistSq(p) <= maxShift * maxShift) moved.Add((w, proj));
        }

        List<int> Run(int c, int dir)
        {
            var idx = new List<int>();
            for (int steps = 1; steps <= n; steps++)
            {
                int j = c + dir * steps;
                int w = closed ? ((j % n) + n) % n : j;
                if (w < 0 || w >= n) break;
                if (pins.Contains(w)) break;
                idx.Add(w);
            }
            return idx;
        }

        // A short side: through its points' centroid, square to the opposite flank unless its
        // own chord clearly says otherwise. Never snapped — a few AA-bent shoulder points are no
        // evidence for an exact 45° direction.
        (Vec2 Pt, Vec2 Dir)? ShortFlank(int c, int dir, (Vec2 Pt, Vec2 Dir) other)
        {
            var idx = Run(c, dir);
            if (idx.Count == 0 || idx.Count >= ShortRun) return null;
            double mx = 0, my = 0;
            foreach (var w in idx)
            {
                mx += pts[w].X;
                my += pts[w].Y;
            }
            var centre = new Vec2(mx / idx.Count, my / idx.Count);
            var square = new Vec2(-other.Dir.Y, other.Dir.X);
            if (idx.Count >= 2)
            {
                var chord = (pts[idx[^1]] - pts[idx[0]]).Normalized();
                if (Math.Abs(chord.Dot(square)) < Math.Cos(35 * Math.PI / 180)) return (centre, chord);
            }
            return (centre, square);
        }

        (Vec2 Pt, Vec2 Dir)? FlankLine(int c, int dir)
        {
            // the run from this corner to the next pin, excluding both pins and one vertex
            // beside each (the smoothing-damped, AA-contaminated shoulders)
            var idx = Run(c, dir);
            if (idx.Count >= 2) idx.RemoveAt(0);
            if (idx.Count >= 2) idx.RemoveAt(idx.Count - 1);
            if (idx.Count < 2) return null;

            // straight run → fit every vertex (both corners of the side then share one line);
            // curved run → only the four vertices nearest the corner
            var a = pts[idx[0]];
            var b = pts[idx[^1]];
            var chord = b - a;
            double len2 = chord.LengthSq;
            bool straight = true;
            if (len2 > 1e-12)
            {
                foreach (var w in idx)
                {
                    double tt = Math.Clamp((pts[w] - a).Dot(chord) / len2, 0, 1);
                    if (pts[w].DistSq(a + chord * tt) > 0.16)   // 0.4 px
                    {
                        straight = false;
                        break;
                    }
                }
            }
            if (!straight && idx.Count > 4) idx.RemoveRange(4, idx.Count - 4);

            double mx = 0, my = 0;
            foreach (var w in idx)
            {
                mx += pts[w].X;
                my += pts[w].Y;
            }
            mx /= idx.Count;
            my /= idx.Count;
            double cxx = 0, cxy = 0, cyy = 0;
            foreach (var w in idx)
            {
                double dx = pts[w].X - mx, dy = pts[w].Y - my;
                cxx += dx * dx;
                cxy += dx * dy;
                cyy += dy * dy;
            }
            if (cxx + cyy < 1e-12) return null;
            double theta = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
            double deg = theta * (180.0 / Math.PI);
            double snapped = Math.Round(deg / 45.0) * 45.0;
            if (Math.Abs(deg - snapped) <= snapDeg) theta = snapped * (Math.PI / 180.0);
            return (new Vec2(mx, my), new Vec2(Math.Cos(theta), Math.Sin(theta)));
        }
    }

    /// <summary>Fewest interior points a run between pins needs before <see cref="StraightenRuns"/> considers it.</summary>
    public const int StraightRunMinPoints = 10;
    /// <summary>Runs already this straight (px) are left alone: there is no ringing to average out,
    /// and re-projecting them only nudges the pinned corners next to them off the true edge.</summary>
    public const double StraightRunMinDev = 0.05;
    /// <summary>Largest distance (px) of any run point from the fitted line for the run to count as straight.</summary>
    public const double StraightRunMaxDev = 1.0;
    /// <summary>Largest bow (px, quadratic residual trend) a straight run may have; above it the run is an arc.</summary>
    public const double StraightRunMaxSag = 0.25;

    /// <summary>
    /// Noisy (JPEG) inputs: ringing shifts an edge differently in every 8×8 block, so a straight
    /// side's refined points wave with a block-length wavelength no local smoothing removes.
    /// Each run between two pins (corners, open-chain ends) that is straight within that noise —
    /// every point within <see cref="StraightRunMaxDev"/> px of the total-least-squares line through all
    /// of them, and no bow (the residuals' quadratic trend, a curve's signature, stays under
    /// <see cref="StraightRunMaxSag"/> px) — is projected onto that one line, averaged over the whole side.
    /// </summary>
    /// <remarks>Only the single point next to each pin is left out of the fit; once a run is
    /// accepted every point of it, ends included, is projected.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void StraightenRuns(List<Vec2> pts, bool closed, IReadOnlyList<int> corners)
    {
        const int minRun = StraightRunMinPoints;
        const double maxDev = StraightRunMaxDev, maxSag = StraightRunMaxSag;
        int n = closed ? pts.Count - 1 : pts.Count;
        if (n < minRun) return;
        var pins = new List<int>(corners);
        if (!closed)
        {
            pins.Add(0);
            pins.Add(n - 1);
        }
        pins = pins.Distinct().OrderBy(i => i).ToList();
        if (pins.Count == 0 || (closed && pins.Count < 2)) return;
        int runs = closed ? pins.Count : pins.Count - 1;
        var idx = new List<int>();
        for (int k = 0; k < runs; k++)
        {
            int a = pins[k], b = k + 1 < pins.Count ? pins[k + 1] : pins[0] + n;
            idx.Clear();
            for (int j = a + 1; j < b; j++) idx.Add(closed ? j % n : j);
            if (idx.Count < minRun) continue;
            // the line comes from the run without the point next to each pin
            double mx = 0, my = 0;
            int lo = 1, hi = idx.Count - 2;
            int m = hi - lo + 1;
            for (int t = lo; t <= hi; t++) { mx += pts[idx[t]].X; my += pts[idx[t]].Y; }
            mx /= m;
            my /= m;
            double cxx = 0, cxy = 0, cyy = 0;
            for (int t = lo; t <= hi; t++)
            {
                double dx = pts[idx[t]].X - mx, dy = pts[idx[t]].Y - my;
                cxx += dx * dx;
                cxy += dx * dy;
                cyy += dy * dy;
            }
            double theta = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
            var dir = new Vec2(Math.Cos(theta), Math.Sin(theta));
            var nrm = new Vec2(-dir.Y, dir.X);
            var c = new Vec2(mx, my);
            // residual r(s) along the line: max deviation and least-squares quadratic trend
            double s0 = double.MaxValue, s1 = double.MinValue, worst = 0;
            for (int t = lo; t <= hi; t++)
            {
                var q = pts[idx[t]] - c;
                double s = q.Dot(dir), r = q.Dot(nrm);
                s0 = Math.Min(s0, s);
                s1 = Math.Max(s1, s);
                worst = Math.Max(worst, Math.Abs(r));
            }
            if (worst > maxDev || worst < StraightRunMinDev) continue;
            double half = (s1 - s0) / 2, mid = (s0 + s1) / 2;
            if (half < 1) continue;
            // fit r = α·u² + β·u + γ with u in [-1, 1]; sagitta of the bow ≈ |α|
            double su0 = 0, su1 = 0, su2 = 0, su3 = 0, su4 = 0, sr0 = 0, sr1 = 0, sr2 = 0;
            for (int t = lo; t <= hi; t++)
            {
                var q = pts[idx[t]] - c;
                double u = (q.Dot(dir) - mid) / half, r = q.Dot(nrm), u2 = u * u;
                su0 += 1; su1 += u; su2 += u2; su3 += u2 * u; su4 += u2 * u2;
                sr0 += r; sr1 += r * u; sr2 += r * u2;
            }
            double det = su4 * (su2 * su0 - su1 * su1) - su3 * (su3 * su0 - su1 * su2) + su2 * (su3 * su1 - su2 * su2);
            if (Math.Abs(det) < 1e-12) continue;
            double alpha = (sr2 * (su2 * su0 - su1 * su1) - su3 * (sr1 * su0 - su1 * sr0) + su2 * (sr1 * su1 - su2 * sr0)) / det;
            if (Math.Abs(alpha) > maxSag) continue;
            for (int t = 0; t < idx.Count; t++)
            {
                var q = pts[idx[t]] - c;
                pts[idx[t]] = c + dir * q.Dot(dir);
            }
        }
        if (closed) pts[n] = pts[0];
    }

    /// <summary>Douglas–Peucker with distance-to-segment (handles closed chains where first == last).</summary>
    public static List<Vec2> SimplifyDp(List<Vec2> pts, double epsilon)
    {
        if (epsilon <= 0 || pts.Count <= 2) return new List<Vec2>(pts);
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, pts.Count - 1));
        double epsSq = epsilon * epsilon;
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            double worst = -1;
            int at = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = DistToSegmentSq(pts[i], pts[a], pts[b]);
                if (d > worst) { worst = d; at = i; }
            }
            if (worst > epsSq)
            {
                keep[at] = true;
                stack.Push((a, at));
                stack.Push((at, b));
            }
        }
        var outPts = new List<Vec2>();
        for (int i = 0; i < pts.Count; i++)
            if (keep[i]) outPts.Add(pts[i]);
        return outPts;
    }

    static double DistToSegmentSq(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double len = ab.LengthSq;
        double t = len < 1e-12 ? 0 : Math.Clamp((p - a).Dot(ab) / len, 0, 1);
        return p.DistSq(a + ab * t);
    }
}
