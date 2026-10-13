using Vecto.Core;

namespace Vecto.Cli;

/// <summary>
/// JPEG decoder that reproduces libjpeg(-turbo)'s default decode bit for bit, so the CLI sees the same pixels as the
/// Windows app (WPF/WIC is byte-identical to libjpeg-turbo / Pillow on our test files; ImageSharp is not). Default
/// libjpeg settings: ISLOW integer IDCT (Loeffler-Ligtenberg-Moschytz, 13-bit constants, 2 extra pass-1 bits),
/// "fancy" triangular chroma upsampling (h2v1 / h2v2 / h1v2, edge rows and columns replicated), 16-bit fixed-point
/// YCbCr->RGB. Written from ITU T.81 and the documented libjpeg arithmetic; no libjpeg source is copied.
/// Scope: 8-bit Huffman JPEGs (baseline, extended, progressive) with 1 or 3 components. Anything else (CMYK/YCCK,
/// 12-bit, arithmetic coding, lossless, unusual sampling) or a decode error returns null and the caller falls back
/// to ImageSharp.
/// </summary>
public static class JpegDecoder
{
    // zig-zag index -> natural index; 16 extra entries absorb run overflow on corrupt data (as libjpeg does)
    static readonly int[] Natural =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63,
    };

    public static bool IsJpeg(byte[] data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    /// <summary>Decodes to RGBA (A = 255), or null when the file is outside the supported scope.</summary>
    public static RasterImage? TryDecode(byte[] data)
    {
        try { return new Decoder(data).Decode(); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IndexOutOfRangeException
                                       or ArgumentException or OverflowException) { return null; }
    }

    sealed class Component
    {
        public int Id, H, V, Tq, Td, Ta, Pred;
        public int Width, Height;          // samples (downsampled size)
        public int BlocksW, BlocksH;       // blocks covering the samples (non-interleaved scan extent)
        public int StrideBlocks, RowsBlocks; // blocks allocated (MCU-padded)
        public short[] Coef = Array.Empty<short>();
        public int[]? Quant;               // latched at the component's first scan
    }

    sealed class Huffman
    {
        public readonly byte[] Values;
        public readonly int[] MaxCode = new int[18], ValPtr = new int[17], MinCode = new int[17];
        public readonly ushort[] Fast = new ushort[512]; // (length << 8) | value for codes up to 9 bits; 0 = slow path

        public Huffman(byte[] counts, byte[] values)
        {
            Values = values;
            int code = 0, k = 0;
            for (int len = 1; len <= 16; len++)
            {
                ValPtr[len] = k;
                MinCode[len] = code;
                for (int i = 0; i < counts[len - 1]; i++, k++, code++)
                {
                    if (k >= values.Length) throw new InvalidDataException("bad Huffman table");
                    if (len <= 9)
                        for (int f = code << (9 - len), e = (code + 1) << (9 - len); f < e; f++)
                            Fast[f] = (ushort)((len << 8) | values[k]);
                }
                MaxCode[len] = counts[len - 1] > 0 ? code - 1 : -1;
                code <<= 1;
            }
            MaxCode[17] = int.MaxValue;
        }
    }

    sealed class BitReader
    {
        readonly byte[] d;
        public int Pos;
        uint buf;
        int cnt;
        bool marker;

        public BitReader(byte[] data, int pos) { d = data; Pos = pos; }

        // past a marker or the end of data, feed zero bits (libjpeg does the same, with a warning)
        void Fill()
        {
            while (cnt <= 24)
            {
                int b = 0;
                if (!marker && Pos < d.Length)
                {
                    b = d[Pos];
                    if (b == 0xFF)
                    {
                        int n = Pos + 1 < d.Length ? d[Pos + 1] : 0xD9;
                        if (n == 0) Pos += 2;
                        else { marker = true; b = 0; }
                    }
                    else Pos++;
                }
                buf |= (uint)b << (24 - cnt);
                cnt += 8;
            }
        }

        public int Peek(int n) { if (cnt < n) Fill(); return (int)(buf >> (32 - n)); }
        public void Skip(int n) { buf <<= n; cnt -= n; }
        public int Bits(int n) { if (n == 0) return 0; int v = Peek(n); Skip(n); return v; }
        public int Bit() => Bits(1);

        public int Decode(Huffman h)
        {
            int f = h.Fast[Peek(9)];
            if (f != 0) { Skip(f >> 8); return f & 0xFF; }
            int code = Peek(16);
            for (int len = 10; len <= 16; len++)
            {
                int c = code >> (16 - len);
                if (c <= h.MaxCode[len]) { Skip(len); return h.Values[h.ValPtr[len] + c - h.MinCode[len]]; }
            }
            Skip(16);
            return 0; // corrupt code: libjpeg also substitutes 0
        }

        public int Receive(int s)
        {
            if (s == 0) return 0;
            int v = Bits(s);
            return v < (1 << (s - 1)) ? v - (1 << s) + 1 : v;
        }

        /// <summary>Drops buffered bits and steps over the expected RSTn marker.</summary>
        public void Restart()
        {
            buf = 0; cnt = 0; marker = false;
            for (int p = Pos; p + 1 < d.Length; p++)
            {
                if (d[p] != 0xFF || d[p + 1] == 0 || d[p + 1] == 0xFF) continue;
                if (d[p + 1] >= 0xD0 && d[p + 1] <= 0xD7) Pos = p + 2;
                else Pos = p; // some other marker: leave it for the parser, the rest of the scan reads zeros
                return;
            }
            Pos = d.Length;
        }
    }

    sealed class Decoder
    {
        readonly byte[] d;
        readonly int[]?[] qt = new int[4][];
        readonly Huffman?[] dcTab = new Huffman[4], acTab = new Huffman[4];
        Component[]? comps;
        int width, height, hmax, vmax, mcusX, mcusY, restartInterval, adobeTransform = -1;
        bool progressive, sawJfif;
        int eobrun;

        public Decoder(byte[] data) { d = data; }

        int U16(int p) => (d[p] << 8) | d[p + 1];

        public RasterImage? Decode()
        {
            if (!IsJpeg(d)) return null;
            int p = 2;
            bool sawScan = false;
            while (true)
            {
                while (p < d.Length && d[p] != 0xFF) p++;
                while (p < d.Length && d[p] == 0xFF) p++;
                if (p >= d.Length) break;
                int m = d[p++];
                if (m == 0xD9) break;
                if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) continue;
                int len = U16(p);
                int seg = p + 2, end = p + len;
                if (len < 2 || end > d.Length) throw new InvalidDataException("truncated segment");
                switch (m)
                {
                    case 0xC0: case 0xC1: case 0xC2:
                        if (comps != null) throw new InvalidDataException("second frame");
                        progressive = m == 0xC2;
                        ReadFrame(seg);
                        break;
                    case 0xC3: case 0xC5: case 0xC6: case 0xC7:
                    case 0xC9: case 0xCA: case 0xCB: case 0xCD: case 0xCE: case 0xCF:
                        return null; // lossless / hierarchical / arithmetic
                    case 0xC4: ReadHuffman(seg, end); break;
                    case 0xDB: ReadQuant(seg, end); break;
                    case 0xDD: restartInterval = U16(seg); break;
                    case 0xE0:
                        if (len >= 7 && d[seg] == 'J' && d[seg + 1] == 'F' && d[seg + 2] == 'I' && d[seg + 3] == 'F' && d[seg + 4] == 0)
                            sawJfif = true;
                        break;
                    case 0xEE:
                        if (len >= 14 && d[seg] == 'A' && d[seg + 1] == 'd' && d[seg + 2] == 'o' && d[seg + 3] == 'b' && d[seg + 4] == 'e')
                            adobeTransform = d[seg + 11];
                        break;
                    case 0xDA:
                        end = ReadScan(seg, end);
                        sawScan = true;
                        break;
                }
                p = end;
            }
            if (comps == null || !sawScan) throw new InvalidDataException("no image data");
            return Output();
        }

        void ReadFrame(int p)
        {
            if (d[p] != 8) throw new NotSupportedException("precision");
            height = U16(p + 1);
            width = U16(p + 3);
            int n = d[p + 5];
            if (height == 0 || width == 0 || (n != 1 && n != 3)) throw new NotSupportedException("frame");
            comps = new Component[n];
            for (int i = 0; i < n; i++)
            {
                int q = p + 6 + i * 3;
                var c = new Component { Id = d[q], H = d[q + 1] >> 4, V = d[q + 1] & 15, Tq = d[q + 2] };
                if (c.H < 1 || c.H > 4 || c.V < 1 || c.V > 4 || c.Tq > 3) throw new InvalidDataException("component");
                comps[i] = c;
            }
            hmax = comps.Max(c => c.H);
            vmax = comps.Max(c => c.V);
            mcusX = (width + 8 * hmax - 1) / (8 * hmax);
            mcusY = (height + 8 * vmax - 1) / (8 * vmax);
            foreach (var c in comps)
            {
                if (hmax % c.H != 0 || vmax % c.V != 0) throw new NotSupportedException("sampling");
                c.Width = (width * c.H + hmax - 1) / hmax;
                c.Height = (height * c.V + vmax - 1) / vmax;
                c.BlocksW = (c.Width + 7) / 8;
                c.BlocksH = (c.Height + 7) / 8;
                c.StrideBlocks = mcusX * c.H;
                c.RowsBlocks = mcusY * c.V;
                c.Coef = new short[c.StrideBlocks * c.RowsBlocks * 64];
            }
        }

        void ReadHuffman(int p, int end)
        {
            while (p < end)
            {
                int tc = d[p] >> 4, th = d[p] & 15;
                if (tc > 1 || th > 3) throw new InvalidDataException("DHT");
                var counts = new byte[16];
                Array.Copy(d, p + 1, counts, 0, 16);
                int total = counts.Sum(b => b);
                if (total > 256) throw new InvalidDataException("DHT");
                var values = new byte[total];
                Array.Copy(d, p + 17, values, 0, total);
                (tc == 0 ? dcTab : acTab)[th] = new Huffman(counts, values);
                p += 17 + total;
            }
        }

        void ReadQuant(int p, int end)
        {
            while (p < end)
            {
                int pq = d[p] >> 4, tq = d[p] & 15;
                if (tq > 3 || pq > 1) throw new InvalidDataException("DQT");
                var q = new int[64];
                for (int i = 0; i < 64; i++)
                    q[Natural[i]] = pq == 0 ? d[p + 1 + i] : U16(p + 1 + 2 * i);
                qt[tq] = q;
                p += 1 + 64 * (pq + 1);
            }
        }

        /// <summary>Reads an SOS header and its entropy-coded data; returns where marker parsing resumes.</summary>
        int ReadScan(int p, int end)
        {
            if (comps == null) throw new InvalidDataException("scan before frame");
            int ns = d[p];
            if (ns < 1 || ns > 4) throw new InvalidDataException("SOS");
            var sc = new Component[ns];
            for (int i = 0; i < ns; i++)
            {
                int id = d[p + 1 + 2 * i];
                var c = comps.FirstOrDefault(x => x.Id == id) ?? throw new InvalidDataException("scan component");
                c.Td = d[p + 2 + 2 * i] >> 4;
                c.Ta = d[p + 2 + 2 * i] & 15;
                if (c.Td > 3 || c.Ta > 3) throw new InvalidDataException("SOS");
                c.Quant ??= (int[])(qt[c.Tq] ?? throw new InvalidDataException("missing quant table")).Clone();
                sc[i] = c;
            }
            int q = p + 1 + 2 * ns;
            int ss = d[q], se = d[q + 1], ah = d[q + 2] >> 4, al = d[q + 2] & 15;
            if (!progressive) { ss = 0; se = 63; ah = 0; al = 0; }
            else if (ss > se || se > 63 || (ss == 0 && se != 0) || (ss > 0 && ns != 1) || al > 13)
                throw new InvalidDataException("progressive parameters");

            foreach (var c in sc)
            {
                c.Pred = 0;
                if ((ss == 0 && ah == 0 && dcTab[c.Td] == null) || (se > 0 && acTab[c.Ta] == null))
                    throw new InvalidDataException("missing Huffman table");
            }
            eobrun = 0;
            var br = new BitReader(d, end);
            bool single = ns == 1;
            int mx = single ? sc[0].BlocksW : mcusX, my = single ? sc[0].BlocksH : mcusY;
            int mcu = 0;
            for (int y = 0; y < my; y++)
                for (int x = 0; x < mx; x++, mcu++)
                {
                    if (restartInterval > 0 && mcu > 0 && mcu % restartInterval == 0)
                    {
                        br.Restart();
                        foreach (var c in sc) c.Pred = 0;
                        eobrun = 0;
                    }
                    if (single)
                        DecodeBlock(br, sc[0], (y * sc[0].StrideBlocks + x) * 64, ss, se, ah, al);
                    else
                        foreach (var c in sc)
                            for (int v = 0; v < c.V; v++)
                                for (int h = 0; h < c.H; h++)
                                    DecodeBlock(br, c, ((y * c.V + v) * c.StrideBlocks + x * c.H + h) * 64, ss, se, ah, al);
                }
            return br.Pos;
        }

        void DecodeBlock(BitReader br, Component c, int o, int ss, int se, int ah, int al)
        {
            var b = c.Coef;
            if (!progressive)
            {
                int t = br.Decode(dcTab[c.Td]!);
                c.Pred += br.Receive(t);
                b[o] = (short)c.Pred;
                var ac = acTab[c.Ta]!;
                for (int k = 1; k < 64; k++)
                {
                    int rs = br.Decode(ac), r = rs >> 4, s = rs & 15;
                    if (s == 0)
                    {
                        if (r != 15) break;
                        k += 15;
                        continue;
                    }
                    k += r;
                    b[o + Natural[k]] = (short)br.Receive(s);
                }
                return;
            }
            if (ss == 0)
            {
                if (ah == 0)
                {
                    int t = br.Decode(dcTab[c.Td]!);
                    c.Pred += br.Receive(t);
                    b[o] = (short)(c.Pred << al);
                }
                else if (br.Bit() != 0) b[o] |= (short)(1 << al);
                return;
            }
            var tab = acTab[c.Ta]!;
            if (ah == 0)
            {
                if (eobrun > 0) { eobrun--; return; }
                for (int k = ss; k <= se; k++)
                {
                    int rs = br.Decode(tab), r = rs >> 4, s = rs & 15;
                    if (s == 0)
                    {
                        if (r < 15)
                        {
                            eobrun = (1 << r) - 1 + br.Bits(r);
                            break;
                        }
                        k += 15;
                        continue;
                    }
                    k += r;
                    b[o + Natural[k]] = (short)(br.Receive(s) * (1 << al));
                }
                return;
            }
            // AC successive-approximation refinement (T.81 G.1.2.3)
            int p1 = 1 << al, m1 = -1 << al;
            int kk = ss;
            if (eobrun == 0)
            {
                for (; kk <= se; kk++)
                {
                    int rs = br.Decode(tab), r = rs >> 4, s = rs & 15;
                    if (s != 0) s = br.Bit() != 0 ? p1 : m1;
                    else if (r != 15)
                    {
                        eobrun = (1 << r) + br.Bits(r);
                        break;
                    }
                    do
                    {
                        int z = o + Natural[kk];
                        if (b[z] != 0)
                        {
                            if (br.Bit() != 0 && (b[z] & p1) == 0) b[z] += (short)(b[z] >= 0 ? p1 : m1);
                        }
                        else if (--r < 0) break;
                        kk++;
                    } while (kk <= se);
                    if (s != 0) b[o + Natural[kk]] = (short)s;
                }
            }
            if (eobrun > 0)
            {
                for (; kk <= se; kk++)
                {
                    int z = o + Natural[kk];
                    if (b[z] != 0 && br.Bit() != 0 && (b[z] & p1) == 0) b[z] += (short)(b[z] >= 0 ? p1 : m1);
                }
                eobrun--;
            }
        }

        RasterImage Output()
        {
            var cs = comps!;
            var full = new byte[cs.Length][];
            for (int i = 0; i < cs.Length; i++)
            {
                var c = cs[i];
                int stride = c.StrideBlocks * 8;
                var plane = new byte[stride * c.RowsBlocks * 8];
                var quant = c.Quant ?? new int[64]; // never scanned (truncated file): all-zero coefficients -> mid grey
                for (int by = 0; by < c.BlocksH; by++)
                    for (int bx = 0; bx < c.BlocksW; bx++)
                        IdctIslow(c.Coef, (by * c.StrideBlocks + bx) * 64, quant, plane, by * 8 * stride + bx * 8, stride);
                full[i] = Upsample(c, plane, stride);
            }

            var px = new byte[width * height * 4];
            int n = width * height;
            if (cs.Length == 1)
            {
                var g = full[0];
                for (int i = 0; i < n; i++) { byte v = g[i]; px[4 * i] = v; px[4 * i + 1] = v; px[4 * i + 2] = v; px[4 * i + 3] = 255; }
            }
            else if (IsRgb())
            {
                for (int i = 0; i < n; i++)
                { px[4 * i] = full[0][i]; px[4 * i + 1] = full[1][i]; px[4 * i + 2] = full[2][i]; px[4 * i + 3] = 255; }
            }
            else
            {
                var yy = full[0];
                var cb = full[1];
                var cr = full[2];
                for (int i = 0; i < n; i++)
                {
                    int y = yy[i], b = cb[i] - 128, r = cr[i] - 128;
                    px[4 * i] = Clamp(y + ((CrR * r + Half) >> 16));
                    px[4 * i + 1] = Clamp(y + ((-CbG * b + Half - CrG * r) >> 16));
                    px[4 * i + 2] = Clamp(y + ((CbB * b + Half) >> 16));
                    px[4 * i + 3] = 255;
                }
            }
            return new RasterImage(width, height, px);
        }

        // libjpeg's colour-space guess for 3-component files
        bool IsRgb()
        {
            if (sawJfif) return false;
            if (adobeTransform >= 0) return adobeTransform == 0;
            var cs = comps!;
            return cs[0].Id == 'R' && cs[1].Id == 'G' && cs[2].Id == 'B';
        }

        /// <summary>Component plane -> full-resolution width x height plane (libjpeg jdsample selection).</summary>
        byte[] Upsample(Component c, byte[] s, int stride)
        {
            int hs = hmax / c.H, vs = vmax / c.V, cw = c.Width, ch = c.Height;
            var o = new byte[width * height];
            if (hs == 1 && vs == 1)
            {
                for (int y = 0; y < height; y++) Array.Copy(s, y * stride, o, y * width, width);
                return o;
            }
            // full upsampled rows, cropped to width on write
            var row = new byte[cw * hs];
            if (hs == 2 && vs == 1 && cw > 2)
            {
                for (int y = 0; y < height; y++)
                {
                    int i = y * stride;
                    int v = s[i];
                    row[0] = (byte)v;
                    row[1] = (byte)((v * 3 + s[i + 1] + 2) >> 2);
                    for (int x = 1; x < cw - 1; x++)
                    {
                        v = s[i + x] * 3;
                        row[2 * x] = (byte)((v + s[i + x - 1] + 1) >> 2);
                        row[2 * x + 1] = (byte)((v + s[i + x + 1] + 2) >> 2);
                    }
                    v = s[i + cw - 1];
                    row[2 * cw - 2] = (byte)((v * 3 + s[i + cw - 2] + 1) >> 2);
                    row[2 * cw - 1] = (byte)v;
                    Array.Copy(row, 0, o, y * width, width);
                }
                return o;
            }
            if (hs == 2 && vs == 2 && cw > 2)
            {
                for (int y = 0; y < height; y++)
                {
                    int r = y >> 1;
                    int nr = (y & 1) == 0 ? Math.Max(r - 1, 0) : Math.Min(r + 1, ch - 1);
                    int i0 = r * stride, i1 = nr * stride;
                    int cur = s[i0] * 3 + s[i1], next = s[i0 + 1] * 3 + s[i1 + 1], last;
                    row[0] = (byte)((cur * 4 + 8) >> 4);
                    row[1] = (byte)((cur * 3 + next + 7) >> 4);
                    last = cur; cur = next;
                    for (int x = 1; x < cw - 1; x++)
                    {
                        next = s[i0 + x + 1] * 3 + s[i1 + x + 1];
                        row[2 * x] = (byte)((cur * 3 + last + 8) >> 4);
                        row[2 * x + 1] = (byte)((cur * 3 + next + 7) >> 4);
                        last = cur; cur = next;
                    }
                    row[2 * cw - 2] = (byte)((cur * 3 + last + 8) >> 4);
                    row[2 * cw - 1] = (byte)((cur * 4 + 7) >> 4);
                    Array.Copy(row, 0, o, y * width, width);
                }
                return o;
            }
            if (hs == 1 && vs == 2)
            {
                for (int y = 0; y < height; y++)
                {
                    int r = y >> 1;
                    bool upper = (y & 1) == 0;
                    int nr = upper ? Math.Max(r - 1, 0) : Math.Min(r + 1, ch - 1);
                    int i0 = r * stride, i1 = nr * stride, bias = upper ? 1 : 2;
                    for (int x = 0; x < width; x++)
                        o[y * width + x] = (byte)((s[i0 + x] * 3 + s[i1 + x] + bias) >> 2);
                }
                return o;
            }
            // other integer ratios (and fancy cases on 1-2 sample wide components): box replication
            for (int y = 0; y < height; y++)
            {
                int i = (y / vs) * stride;
                for (int x = 0; x < width; x++) o[y * width + x] = s[i + x / hs];
            }
            return o;
        }
    }

    // YCbCr->RGB, libjpeg fixed point: 16 fraction bits, FIX(x) = round(x * 65536)
    const int CrR = 91881, CbB = 116130, CrG = 46802, CbG = 22554, Half = 1 << 15;

    static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // IDCT output range limit: libjpeg masks the descaled value to 10 bits and looks it up in a table that clamps
    // [-512, 511] to [0, 255] around the +128 level shift (and wraps beyond, only reachable on corrupt data)
    static byte RangeLimit(int x)
    {
        x &= 1023;
        return x < 128 ? (byte)(x + 128) : x < 512 ? (byte)255 : x < 896 ? (byte)0 : (byte)(x - 896);
    }

    const int ConstBits = 13, Pass1Bits = 2;
    const int F0298 = 2446, F0390 = 3196, F0541 = 4433, F0765 = 6270, F0899 = 7373, F1175 = 9633,
              F1501 = 12299, F1847 = 15137, F1961 = 16069, F2053 = 16819, F2562 = 20995, F3072 = 25172;

    /// <summary>libjpeg ISLOW: separable 8-point LLM IDCT in 32-bit integer arithmetic, columns then rows.</summary>
    static void IdctIslow(short[] coef, int ci, int[] q, byte[] dst, int di, int stride)
    {
        Span<int> ws = stackalloc int[64];
        for (int col = 0; col < 8; col++)
        {
            int c = ci + col;
            int i0 = coef[c] * q[col], i1 = coef[c + 8] * q[col + 8], i2 = coef[c + 16] * q[col + 16],
                i3 = coef[c + 24] * q[col + 24], i4 = coef[c + 32] * q[col + 32], i5 = coef[c + 40] * q[col + 40],
                i6 = coef[c + 48] * q[col + 48], i7 = coef[c + 56] * q[col + 56];
            Butterfly(i0, i1, i2, i3, i4, i5, i6, i7, out int o0, out int o1, out int o2, out int o3,
                      out int o4, out int o5, out int o6, out int o7, ConstBits - Pass1Bits);
            ws[col] = o0; ws[col + 8] = o1; ws[col + 16] = o2; ws[col + 24] = o3;
            ws[col + 32] = o4; ws[col + 40] = o5; ws[col + 48] = o6; ws[col + 56] = o7;
        }
        for (int row = 0; row < 8; row++)
        {
            int w = row * 8;
            Butterfly(ws[w], ws[w + 1], ws[w + 2], ws[w + 3], ws[w + 4], ws[w + 5], ws[w + 6], ws[w + 7],
                      out int o0, out int o1, out int o2, out int o3, out int o4, out int o5, out int o6, out int o7,
                      ConstBits + Pass1Bits + 3);
            int o = di + row * stride;
            dst[o] = RangeLimit(o0); dst[o + 1] = RangeLimit(o1); dst[o + 2] = RangeLimit(o2); dst[o + 3] = RangeLimit(o3);
            dst[o + 4] = RangeLimit(o4); dst[o + 5] = RangeLimit(o5); dst[o + 6] = RangeLimit(o6); dst[o + 7] = RangeLimit(o7);
        }
    }

    // one 1-D pass; outputs are descaled by `shift` with round-half-up (libjpeg DESCALE). libjpeg's all-zero-AC
    // shortcuts give the same values as the full computation, so they are not reproduced.
    static void Butterfly(int i0, int i1, int i2, int i3, int i4, int i5, int i6, int i7,
                          out int o0, out int o1, out int o2, out int o3, out int o4, out int o5, out int o6, out int o7,
                          int shift)
    {
        // even part
        int z1 = (i2 + i6) * F0541;
        int tmp2 = z1 - i6 * F1847;
        int tmp3 = z1 + i2 * F0765;
        int tmp0 = (i0 + i4) << ConstBits;
        int tmp1 = (i0 - i4) << ConstBits;
        int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;

        // odd part
        int t0 = i7, t1 = i5, t2 = i3, t3 = i1;
        z1 = t0 + t3;
        int z2 = t1 + t2, z3 = t0 + t2, z4 = t1 + t3;
        int z5 = (z3 + z4) * F1175;
        t0 *= F0298; t1 *= F2053; t2 *= F3072; t3 *= F1501;
        z1 *= -F0899; z2 *= -F2562; z3 *= -F1961; z4 *= -F0390;
        z3 += z5; z4 += z5;
        t0 += z1 + z3; t1 += z2 + z4; t2 += z2 + z3; t3 += z1 + z4;

        int round = 1 << (shift - 1);
        o0 = (tmp10 + t3 + round) >> shift; o7 = (tmp10 - t3 + round) >> shift;
        o1 = (tmp11 + t2 + round) >> shift; o6 = (tmp11 - t2 + round) >> shift;
        o2 = (tmp12 + t1 + round) >> shift; o5 = (tmp12 - t1 + round) >> shift;
        o3 = (tmp13 + t0 + round) >> shift; o4 = (tmp13 - t0 + round) >> shift;
    }
}
