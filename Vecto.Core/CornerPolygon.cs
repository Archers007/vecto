namespace Vecto.Core;

/// <summary>
/// Structural corner detection after Selinger, "Potrace: a polygon-based tracing algorithm"
/// (2003), §2.2–2.3, implemented from the paper. The staircase chain is first reduced to its
/// optimal polygon — the fewest straight segments (each within a ½-px max-norm band, extended
/// by one vertex at both ends), ties broken by a deviation penalty. Corner decisions are then
/// made per polygon vertex from its own two edges only, so two 90° corners three lattice steps
/// apart (the short end of a thin bar) are two separate vertices and both survive, while a
/// circle's polygon has many short, shallow turns and none.
/// </summary>
internal static class CornerPolygon
{
    /// <summary>Cap on one straight run's look-ahead; bounds the O(n·m) tables on long sides.</summary>
    const int MaxRun = 24;
    /// <summary>Closed chains: number of start offsets tried for the cyclic optimum.</summary>
    const int MaxStarts = 12;

    /// <summary>
    /// Polygon vertex indices (ascending, into the lattice chain). Open chains always include
    /// both endpoints; closed chains (first == last point) list each vertex once.
    /// </summary>
    public static List<int> OptimalPolygon(List<Vec2> pts, bool closed)
    {
        int n = closed ? pts.Count - 1 : pts.Count;
        var result = new List<int>();
        if (n < 2) return result;
        if (closed && n < 4)
        {
            for (int i = 0; i < n; i++) result.Add(i);
            return result;
        }
        var origin = pts[0];
        int len = closed ? 3 * n : n;   // closed: unrolled so every start offset sees a full turn
        var px = new double[len];
        var py = new double[len];
        for (int i = 0; i < len; i++)
        {
            var q = pts[i % n] - origin;
            px[i] = q.X;
            py[i] = q.Y;
        }
        var reach = ReachLengths(px, py, n, closed);
        var sums = new PrefixSums(px, py);

        if (!closed)
        {
            var path = ShortestPath(0, n - 1, i => MaxEnd(i));
            result.AddRange(path!.Value.Vertices);
            return result;
        }

        // every polygon has a vertex within one clipped run of any index: start from the
        // tightest spot (usually a sharp corner) and try the offsets inside its run
        int s0 = 0;
        for (int i = 1; i < n; i++)
            if (reach[i] < reach[s0]) s0 = i;
        int starts = Math.Min(Math.Max(1, reach[s0] - 1), MaxStarts);
        (int Count, double Penalty, List<int> Vertices)? best = null;
        for (int k = 0; k < starts; k++)
        {
            int st = s0 + k;
            var path = ShortestPath(st, st + n, i => MaxEnd(i));
            if (path is not { } p) continue;
            if (best is not { } b || p.Count < b.Count || (p.Count == b.Count && p.Penalty < b.Penalty - 1e-9))
                best = p;
        }
        foreach (var v in best!.Value.Vertices) result.Add(v % n);
        result.Sort();
        return result;

        int MaxEnd(int i)
        {
            if (closed)
            {
                int lo = ((i - 1) % n + n) % n;
                return i + Math.Min(reach[lo] - 2, n - 3);
            }
            int l = Math.Max(i - 1, 0);
            int far = l + reach[l];
            return far >= n - 1 ? n - 1 : far - 1;
        }

        (int Count, double Penalty, List<int> Vertices)? ShortestPath(int from, int to, Func<int, int> maxEnd)
        {
            int m = to - from;
            var cnt = new int[m + 1];
            var pen = new double[m + 1];
            var prev = new int[m + 1];
            Array.Fill(cnt, int.MaxValue);
            cnt[0] = 0;
            for (int a = 0; a < m; a++)
            {
                if (cnt[a] == int.MaxValue) continue;
                int i = from + a;
                int hi = Math.Min(maxEnd(i), to);
                for (int j = i + 1; j <= hi; j++)
                {
                    int bIdx = j - from;
                    int c = cnt[a] + 1;
                    if (c > cnt[bIdx]) continue;
                    double pp = pen[a] + sums.Penalty(i, j);
                    if (c < cnt[bIdx] || pp < pen[bIdx] - 1e-12)
                    {
                        cnt[bIdx] = c;
                        pen[bIdx] = pp;
                        prev[bIdx] = a;
                    }
                }
            }
            if (cnt[m] == int.MaxValue) return null;
            var vs = new List<int>();
            for (int b = m; b > 0; b = prev[b]) vs.Add(from + b);
            vs.Add(from);
            vs.Reverse();
            vs.RemoveAt(vs.Count - 1);   // closed: the end repeats the start
            if (!closed) vs.Add(to);
            return (cnt[m], pen[m], vs);
        }
    }

    /// <summary>
    /// Corner candidates: polygon vertices (never open-chain endpoints) whose turn between the
    /// two adjacent polygon edges reaches <paramref name="minTurnDeg"/>. The ½-px band lets an
    /// optimal vertex sit a lattice step or two off the actual corner, so each candidate first
    /// moves (within ±2 steps, between its neighbours) to the point farthest from the chord of
    /// its two neighbours; <paramref name="poly"/> is updated to match.
    /// </summary>
    public static List<int> Corners(List<Vec2> pts, bool closed, List<int> poly, double minTurnDeg)
    {
        var result = new List<int>();
        int m = poly.Count;
        if (m < 3) return result;
        int n = closed ? pts.Count - 1 : pts.Count;
        for (int k = closed ? 0 : 1; k < (closed ? m : m - 1); k++)
        {
            int ia = poly[(k - 1 + m) % m], iv = poly[k], ib = poly[(k + 1) % m];
            var a = pts[ia];
            var b = pts[ib];
            if (Turn(pts[iv] - a, b - pts[iv]) < minTurnDeg) continue;
            // the lattice lines of both edges (their shoulders excluded) meet at the corner
            var lIn = LatticeLine(iv, ia, -1);
            var lOut = LatticeLine(iv, ib, +1);
            int best = iv;
            double cr = Cross(lIn.Dir, lOut.Dir);
            if (Math.Abs(cr) > 0.2)
            {
                double t = Cross(lOut.Pt - lIn.Pt, lOut.Dir) / cr;
                var apex = lIn.Pt + lIn.Dir * t;
                int back = Steps(ia, iv), fwd = Steps(iv, ib);
                double bestDist = pts[iv].DistSq(apex);
                for (int d = -2; d <= 2; d++)
                {
                    if (d == 0 || d <= -back || d >= fwd) continue;
                    int w = Wrap(iv + d);
                    double dist = pts[w].DistSq(apex);
                    if (dist < bestDist - 1e-9)
                    {
                        bestDist = dist;
                        best = w;
                    }
                }
            }
            poly[k] = best;
            result.Add(best);
        }
        poly.Sort();
        result.Sort();
        return result;

        int Steps(int from, int to) => closed ? ((to - from) % n + n) % n : to - from;
        int Wrap(int i) => closed ? ((i % n) + n) % n : i;

        (Vec2 Pt, Vec2 Dir) LatticeLine(int from, int to, int dir)
        {
            int steps = dir > 0 ? Steps(from, to) : Steps(to, from);
            var span = new List<Vec2>();
            int skip = steps >= 8 ? 2 : 0;
            for (int s = skip; s <= Math.Min(steps, 16); s++) span.Add(pts[Wrap(from + dir * s)]);
            return PrincipalLine(span);
        }
    }

    /// <summary>Centroid and principal direction (oriented first → last) of a point run.</summary>
    static (Vec2 Pt, Vec2 Dir) PrincipalLine(List<Vec2> span)
    {
        double mx = 0, my = 0;
        foreach (var q in span) { mx += q.X; my += q.Y; }
        mx /= span.Count;
        my /= span.Count;
        double cxx = 0, cxy = 0, cyy = 0;
        foreach (var q in span)
        {
            double dx = q.X - mx, dy = q.Y - my;
            cxx += dx * dx;
            cxy += dx * dy;
            cyy += dy * dy;
        }
        double theta = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
        var d = new Vec2(Math.Cos(theta), Math.Sin(theta));
        return (new Vec2(mx, my), d.Dot(span[^1] - span[0]) < 0 ? -d : d);
    }

    /// <summary>
    /// Corner validation on the current (refined) geometry, using only the vertex's own two
    /// polygon edges — never a window that reaches a neighbouring corner. Flank lines are the
    /// principal directions of the points along each edge; the corner stays when (1) their turn
    /// reaches <paramref name="minTurnDeg"/> and (2) the source image actually fills the corner:
    /// at the flank-line intersection the convex side's coverage is about interior angle / 360
    /// for a real corner, but near zero where a curve (a small circle) cuts across it.
    /// </summary>
    public static bool Validate(List<Vec2> pts, bool closed, IReadOnlyList<int> corners, int c, double minTurnDeg,
        RasterImage img, Rgba32? left, Rgba32? right)
    {
        int n = closed ? pts.Count - 1 : pts.Count;
        var pinned = new HashSet<int>(corners);
        var (pIn, dIn, nIn) = Flank(-1);
        var (pOut, dOut, nOut) = Flank(+1);
        if (nIn == 0 || nOut == 0) return false;
        dIn = -dIn;   // pointing into the corner
        double turn = Turn(dIn, dOut);
        bool shortIn = nIn < ChainGeometry.ShortRun, shortOut = nOut < ChainGeometry.ShortRun;
        // both edges short: a small round shape's polygon is the usual suspect — demand more
        double need = nIn < 8 && nOut < 8 ? 0.6 : 0.3;
        if (shortIn == shortOut)
            return turn >= minTurnDeg && Filled(dIn, dOut, turn, need);
        // a thin bar's end: two to five AA-bent points whose own direction is mostly noise, so
        // the lattice polygon's turn stands and the image decides — with a square end, or the
        // end's own direction when that is a clear corner too
        var longDir = shortIn ? dOut : dIn;
        var sq = new Vec2(-longDir.Y, longDir.X);
        var shortDir = shortIn ? dIn : dOut;
        if (sq.Dot(shortDir) < 0) sq = -sq;
        if (Filled(shortIn ? sq : dIn, shortIn ? dOut : sq, 90, 0.3)) return true;
        return turn >= minTurnDeg && Filled(dIn, dOut, turn, need);

        // does the source fill the corner of these two flank lines? At their intersection the
        // wedge side's coverage is about (180° − turn) / 360° for a real corner
        bool Filled(Vec2 din, Vec2 dout, double turnDeg, double need)
        {
            if (left == null && right == null) return true;
            double cross = Cross(din, dout);
            if (Math.Abs(cross) < 0.2) return true;
            double t = Cross(pOut - pIn, dout) / cross;
            var apex = pIn + din * t;
            if (apex.DistSq(pts[c]) > 4) return true;   // implausible apex: no evidence either way
            double coverLeft = ChainGeometry.CoverLeft(img, left, right, apex);
            if (double.IsNaN(coverLeft)) return true;
            // turning left (cross < 0 in y-down coordinates) puts the wedge on the left side
            double cover = cross < 0 ? coverLeft : 1 - coverLeft;
            double expected = (180 - turnDeg) / 360;
            return cover >= need * expected;
        }

        // centroid + direction (outward) of the run from the corner to the next corner, at most
        // ten points; the count is the run length before the shoulder is dropped
        (Vec2 Pt, Vec2 Dir, int Count) Flank(int dir)
        {
            const int cap = 10;
            var span = new List<Vec2>();
            for (int s = 1; s <= cap; s++)
            {
                int j = c + dir * s;
                int w = closed ? ((j % n) + n) % n : j;
                if (w < 0 || w >= n || pinned.Contains(w)) break;
                span.Add(pts[w]);
                if (!closed && (w == 0 || w == n - 1)) break;
            }
            int count = span.Count;
            if (count == 0) return (pts[c], new Vec2(0, 0), 0);
            if (span.Count >= 4) span.RemoveAt(0);   // the AA-bent shoulder
            if (span.Count < 2) span.Insert(0, pts[c]);
            var (pt, d) = PrincipalLine(span);
            return (pt, d, count);
        }
    }

    static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    static double Turn(Vec2 v1, Vec2 v2)
    {
        double l1 = v1.Length, l2 = v2.Length;
        if (l1 < 1e-9 || l2 < 1e-9) return 0;
        double cos = Math.Clamp(v1.Dot(v2) / (l1 * l2), -1.0, 1.0);
        return Math.Acos(cos) * (180.0 / Math.PI);
    }

    /// <summary>
    /// reach[s] = number of steps from s that stay straight: lattice points s..s+reach[s] all
    /// lie within ½ px (max norm) of one line, and the run does not use all four directions.
    /// Incremental: the feasible lines y = a·x + c (|a| ≤ 1, and the steep mirror x = a·y + c)
    /// form a convex region in (a, c) per slope sign; each point clips it by two half-planes.
    /// </summary>
    static int[] ReachLengths(double[] px, double[] py, int n, bool closed)
    {
        var reach = new int[n];
        var regions = new Region[4];
        for (int r = 0; r < 4; r++) regions[r] = new Region();
        var scratch = new Region();
        for (int s = 0; s < n; s++)
        {
            for (int r = 0; r < 4; r++)
                regions[r].Reset(r % 2 == 0 ? 0 : -1, r % 2 == 0 ? 1 : 0);
            int dirs = 0;
            int maxT = closed ? Math.Min(n - 1, MaxRun) : Math.Min(n - 1 - s, MaxRun);
            int t = 0;
            while (t < maxT)
            {
                int k = s + t + 1;
                double dx = px[k] - px[k - 1], dy = py[k] - py[k - 1];
                int d = dx > 0.5 ? 1 : dx < -0.5 ? 2 : dy > 0.5 ? 4 : 8;
                if ((dirs | d) == 15) break;
                double x = px[k] - px[s], y = py[k] - py[s];
                bool any = false;
                for (int r = 0; r < 4; r++)
                {
                    var reg = regions[r];
                    if (reg.Count == 0) continue;
                    bool steep = r >= 2;
                    double u = steep ? y : x, v = steep ? x : y;   // line v = a·u + c
                    // the line meets the square around (u, v) iff its range over
                    // [u - ½, u + ½] overlaps [v - ½, v + ½]
                    double lo = r % 2 == 0 ? u - 0.5 : u + 0.5, hi = r % 2 == 0 ? u + 0.5 : u - 0.5;
                    reg.Clip(lo, 1, v + 0.5, scratch);       // a·lo + c ≤ v + ½
                    reg.Clip(-hi, -1, -(v - 0.5), scratch);  // a·hi + c ≥ v − ½
                    if (reg.Count > 0) any = true;
                }
                if (!any) break;
                dirs |= d;
                t++;
            }
            reach[s] = t;
        }
        return reach;
    }

    /// <summary>A small convex polygon in (a, c) line-parameter space, clipped in place.</summary>
    sealed class Region
    {
        const int Cap = 48;
        double[] _a = new double[Cap], _c = new double[Cap];
        public int Count;

        public void Reset(double aLo, double aHi)
        {
            _a[0] = aLo; _c[0] = -2;
            _a[1] = aHi; _c[1] = -2;
            _a[2] = aHi; _c[2] = 2;
            _a[3] = aLo; _c[3] = 2;
            Count = 4;
        }

        /// <summary>Sutherland–Hodgman clip by α·a + β·c ≤ γ (closed, tiny slack).</summary>
        public void Clip(double alpha, double beta, double gamma, Region scratch)
        {
            if (Count == 0) return;
            const double eps = 1e-6;   // lattice lines touch the ½-px band exactly all the time
            int m = 0;
            var oa = scratch._a;
            var oc = scratch._c;
            double pa = _a[Count - 1], pc = _c[Count - 1];
            double fp = alpha * pa + beta * pc - gamma;
            for (int i = 0; i < Count && m < Cap - 1; i++)
            {
                double qa = _a[i], qc = _c[i];
                double fq = alpha * qa + beta * qc - gamma;
                bool inP = fp <= eps, inQ = fq <= eps;
                if (inP != inQ)
                {
                    double t = fp / (fp - fq);
                    oa[m] = pa + (qa - pa) * t;
                    oc[m] = pc + (qc - pc) * t;
                    m++;
                }
                if (inQ)
                {
                    oa[m] = qa;
                    oc[m] = qc;
                    m++;
                }
                pa = qa;
                pc = qc;
                fp = fq;
            }
            (_a, scratch._a) = (scratch._a, _a);
            (_c, scratch._c) = (scratch._c, _c);
            Count = m;
        }
    }

    /// <summary>
    /// Segment penalty (paper §2.2.3 in spirit): chord length × RMS distance of the covered
    /// lattice points from the chord, O(1) from prefix sums.
    /// </summary>
    sealed class PrefixSums
    {
        readonly double[] _x, _y, _xx, _xy, _yy;
        readonly double[] _px, _py;

        public PrefixSums(double[] px, double[] py)
        {
            int len = px.Length;
            _px = px;
            _py = py;
            _x = new double[len + 1];
            _y = new double[len + 1];
            _xx = new double[len + 1];
            _xy = new double[len + 1];
            _yy = new double[len + 1];
            for (int i = 0; i < len; i++)
            {
                _x[i + 1] = _x[i] + px[i];
                _y[i + 1] = _y[i] + py[i];
                _xx[i + 1] = _xx[i] + px[i] * px[i];
                _xy[i + 1] = _xy[i] + px[i] * py[i];
                _yy[i + 1] = _yy[i] + py[i] * py[i];
            }
        }

        public double Penalty(int i, int j)
        {
            double ex = _px[j] - _px[i], ey = _py[j] - _py[i];
            double l = Math.Sqrt(ex * ex + ey * ey);
            if (l < 1e-12) return 0;
            double ux = ex / l, uy = ey / l;
            int c = j - i + 1;
            double sx = _x[j + 1] - _x[i], sy = _y[j + 1] - _y[i];
            double sxx = _xx[j + 1] - _xx[i], sxy = _xy[j + 1] - _xy[i], syy = _yy[j + 1] - _yy[i];
            // signed distance from the chord = w(p) - w(p_i), w(p) = x·uy - y·ux
            double sw = uy * sx - ux * sy;
            double sww = uy * uy * sxx - 2 * ux * uy * sxy + ux * ux * syy;
            double w0 = _px[i] * uy - _py[i] * ux;
            double ss = Math.Max(0, sww - 2 * w0 * sw + c * w0 * w0);
            return l * Math.Sqrt(ss / c);
        }
    }
}
