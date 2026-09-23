using System;
using System.Text;
using Newtonsoft.Json;
using VDGS.Vp8l;

namespace VDGS.Sog
{
    public sealed class SogSplats
    {
        public int Count;
        public float[] Pos;        // 3n, x y z as stored (no mirror)
        public float[] Scale;      // 3n, linear (exp applied)
        public float[] Rot;        // 4n, x y z w, unit length
        public float[] Color;      // 3n, 0.5 + C0 * dc
        public float[] Opacity;    // n, 0..1
        public int ShBands;        // 0..3
        public ushort[] ShLabel;   // n; empty when ShBands == 0
        public float[] ShPalette;  // (PaletteCount + 1) * 45; the last row is zero, coefficient-major rgb triples
                                   // [k*3]=R_k [k*3+1]=G_k [k*3+2]=B_k, k < 15; zero past the bands
        public int PaletteCount;   // shN.count, or 0
    }

    public static class SogChunk
    {
        private static readonly int[] ShCoeffs = { 0, 3, 8, 15 };
        // 33.5M: room for a whole 17M scene bundled as one .sog, and it bounds every image.
        public const int MaxChunkSplats = 1 << 25;
        private const float ShC0 = 0.2820948f;
        private static readonly float InvSqrt2 = 1f / (float)Math.Sqrt(2.0);

        /// <summary>
        /// metaPath is relative to the SOG root; image names resolve beside it.
        /// </summary>
        public static SogSplats Decode(ISogFiles files, string metaPath)
        {
            if (files == null) throw new SogException("null files");
            if (string.IsNullOrEmpty(metaPath)) throw new SogException("empty meta path");

            byte[] metaBytes = files.Read(metaPath);
            SogMeta meta;
            try
            {
                meta = JsonConvert.DeserializeObject<SogMeta>(Encoding.UTF8.GetString(metaBytes));
            }
            catch (Exception e)
            {
                throw new SogException("meta.json parse failed: " + e.Message);
            }
            if (meta == null) throw new SogException("meta.json empty");
            if (meta.Version != 2)
                throw new SogException("unsupported SOG version: " + meta.Version + " (need 2)");
            if (meta.Count < 0) throw new SogException("negative count");
            if (meta.Count > MaxChunkSplats)
                throw new SogException("count " + meta.Count + " exceeds " + MaxChunkSplats + " per chunk");
            if (meta.Means == null || meta.Means.Files == null || meta.Means.Files.Length < 2)
                throw new SogException("means.files needs two entries");
            if (meta.Means.Mins == null || meta.Means.Maxs == null
                || meta.Means.Mins.Length < 3 || meta.Means.Maxs.Length < 3)
                throw new SogException("means mins/maxs incomplete");
            if (meta.Scales == null || meta.Scales.Files == null || meta.Scales.Files.Length < 1
                || meta.Scales.Codebook == null)
                throw new SogException("scales incomplete");
            if (meta.Quats == null || meta.Quats.Files == null || meta.Quats.Files.Length < 1)
                throw new SogException("quats incomplete");
            if (meta.Sh0 == null || meta.Sh0.Files == null || meta.Sh0.Files.Length < 1
                || meta.Sh0.Codebook == null)
                throw new SogException("sh0 incomplete");

            string dir = MetaDir(metaPath);
            int n = meta.Count;

            byte[] meansL = DecodeRgba(files, Join(dir, meta.Means.Files[0]), n, "means_l");
            byte[] meansU = DecodeRgba(files, Join(dir, meta.Means.Files[1]), n, "means_u");
            byte[] quats = DecodeRgba(files, Join(dir, meta.Quats.Files[0]), n, "quats");
            byte[] scales = DecodeRgba(files, Join(dir, meta.Scales.Files[0]), n, "scales");
            byte[] sh0 = DecodeRgba(files, Join(dir, meta.Sh0.Files[0]), n, "sh0");

            float[] sCode = ToFloatCodebook(meta.Scales.Codebook);
            float[] cCode = ToFloatCodebook(meta.Sh0.Codebook);
            float xMin = (float)meta.Means.Mins[0], xMax = (float)meta.Means.Maxs[0];
            float yMin = (float)meta.Means.Mins[1], yMax = (float)meta.Means.Maxs[1];
            float zMin = (float)meta.Means.Mins[2], zMax = (float)meta.Means.Maxs[2];
            // No guard on a collapsed axis: this is a multiplier, so zero width decodes to
            // the axis minimum, which is what the reference decoder produces.
            float xScale = xMax - xMin, yScale = yMax - yMin, zScale = zMax - zMin;

            var s = new SogSplats
            {
                Count = n,
                Pos = new float[n * 3],
                Scale = new float[n * 3],
                Rot = new float[n * 4],
                Color = new float[n * 3],
                Opacity = new float[n],
                ShBands = 0,
                ShLabel = Array.Empty<ushort>(),
                ShPalette = Array.Empty<float>(),
                PaletteCount = 0
            };

            for (int i = 0; i < n; i++)
            {
                int o4 = i * 4;
                int qx = meansL[o4] | (meansU[o4] << 8);
                int qy = meansL[o4 + 1] | (meansU[o4 + 1] << 8);
                int qz = meansL[o4 + 2] | (meansU[o4 + 2] << 8);
                int p = i * 3;
                s.Pos[p] = InvLog(xMin + xScale * (qx / 65535f));
                s.Pos[p + 1] = InvLog(yMin + yScale * (qy / 65535f));
                s.Pos[p + 2] = InvLog(zMin + zScale * (qz / 65535f));

                UnpackQuat(quats[o4], quats[o4 + 1], quats[o4 + 2], quats[o4 + 3], s.Rot, i * 4);

                s.Scale[p] = (float)Math.Exp(Lookup(sCode, scales[o4]));
                s.Scale[p + 1] = (float)Math.Exp(Lookup(sCode, scales[o4 + 1]));
                s.Scale[p + 2] = (float)Math.Exp(Lookup(sCode, scales[o4 + 2]));

                s.Color[p] = 0.5f + Lookup(cCode, sh0[o4]) * ShC0;
                s.Color[p + 1] = 0.5f + Lookup(cCode, sh0[o4 + 1]) * ShC0;
                s.Color[p + 2] = 0.5f + Lookup(cCode, sh0[o4 + 2]) * ShC0;
                s.Opacity[i] = sh0[o4 + 3] / 255f;
            }

            if (meta.ShN != null && meta.ShN.Bands >= 1 && meta.ShN.Bands <= 3
                && ShCoeffs[meta.ShN.Bands] > 0)
            {
                if (meta.ShN.Files == null || meta.ShN.Files.Length < 2 || meta.ShN.Codebook == null)
                    throw new SogException("shN incomplete");
                int bands = meta.ShN.Bands;
                int coeffs = ShCoeffs[bands];
                int paletteCount = meta.ShN.Count;
                if (paletteCount < 0) throw new SogException("shN.count negative");
                // Labels are 16-bit, so a chunk cannot address more rows than this.
                if (paletteCount > 65536) throw new SogException("shN.count " + paletteCount + " exceeds 65536");

                long maxCentroidPixels = 64L * coeffs * (paletteCount / 64 + 2);
                byte[] centroids = DecodeRgbaRaw(files, Join(dir, meta.ShN.Files[0]), maxCentroidPixels,
                                                 out int cW, out int cH);
                if (cW != 64 * coeffs)
                    throw new SogException(
                        "shN centroids width " + cW + " != expected " + (64 * coeffs)
                        + " for " + bands + "-band palette");
                byte[] labels = DecodeRgba(files, Join(dir, meta.ShN.Files[1]), n, "shN_labels");
                float[] shCode = ToFloatCodebook(meta.ShN.Codebook);

                // +1 zero row for out-of-range labels.
                int rows = paletteCount + 1;
                var palette = new float[rows * 45];
                for (int p = 0; p < paletteCount; p++)
                {
                    int row = p / 64;
                    int cxBase = (p % 64) * coeffs;
                    int baseOff = p * 45;
                    bool rowOk = row < cH;
                    for (int j = 0; j < coeffs; j++)
                    {
                        int cx = cxBase + j;
                        int idx = (row * cW + cx) * 4;
                        byte br = 0, bg = 0, bb = 0;
                        if (rowOk && cx < cW && idx + 2 < centroids.Length)
                        {
                            br = centroids[idx];
                            bg = centroids[idx + 1];
                            bb = centroids[idx + 2];
                        }
                        int o = baseOff + j * 3;
                        palette[o] = Lookup(shCode, br);
                        palette[o + 1] = Lookup(shCode, bg);
                        palette[o + 2] = Lookup(shCode, bb);
                    }
                    // coefficients past the band stay zero (already).
                }

                var shLabel = new ushort[n];
                for (int i = 0; i < n; i++)
                {
                    int o4 = i * 4;
                    int label = labels[o4] | (labels[o4 + 1] << 8);
                    if (label >= paletteCount) label = paletteCount; // zero row
                    shLabel[i] = (ushort)label;
                }

                s.ShBands = bands;
                s.PaletteCount = paletteCount;
                s.ShPalette = palette;
                s.ShLabel = shLabel;
            }

            return s;
        }

        private static string MetaDir(string metaPath)
        {
            int slash = metaPath.LastIndexOf('/');
            return slash < 0 ? "" : metaPath.Substring(0, slash);
        }

        private static string Join(string dir, string name)
        {
            if (string.IsNullOrEmpty(dir)) return name.Replace('\\', '/');
            return dir + "/" + name.Replace('\\', '/');
        }

        private static byte[] DecodeRgba(ISogFiles files, string path, int count, string what)
        {
            // A per-splat image needs count pixels; twice that plus slack covers any row padding.
            byte[] rgba = DecodeRgbaRaw(files, path, 2L * count + 65536, out int w, out int h);
            if ((long)w * h < count)
                throw new SogException(what + " texture too small: " + w + "x" + h + " for count " + count);
            return rgba;
        }

        private static byte[] DecodeRgbaRaw(ISogFiles files, string path, long maxPixels, out int w, out int h)
        {
            byte[] webp = files.Read(path);
            try
            {
                return Vp8lDecoder.Decode(webp, out w, out h, maxPixels);
            }
            catch (Vp8lException e)
            {
                throw new SogException(path + ": " + e.Message);
            }
        }

        private static float[] ToFloatCodebook(double[] src)
        {
            var dst = new float[src.Length];
            for (int i = 0; i < src.Length; i++) dst[i] = (float)src[i];
            return dst;
        }

        private static float Lookup(float[] codebook, int index)
        {
            if ((uint)index >= (uint)codebook.Length) return 0f;
            return codebook[index];
        }

        /// <summary>Inverse of logTransform(x) = sign(x) * ln(|x| + 1).</summary>
        private static float InvLog(float n)
        {
            float a = Math.Abs(n);
            float e = (float)Math.Exp(a) - 1f;
            return n < 0f ? -e : e;
        }

        /// <summary>Writes x y z w into rot[off..off+3].</summary>
        private static void UnpackQuat(byte px, byte py, byte pz, byte tag, float[] rot, int off)
        {
            if (tag < 252 || tag > 255)
            {
                rot[off] = 0f;
                rot[off + 1] = 0f;
                rot[off + 2] = 0f;
                rot[off + 3] = 1f;
                return;
            }
            int m = tag - 252;
            float a = (px / 255f * 2f - 1f) * InvSqrt2;
            float b = (py / 255f * 2f - 1f) * InvSqrt2;
            float c = (pz / 255f * 2f - 1f) * InvSqrt2;
            // a,b,c fill [w,x,y,z] minus the dropped component m, which is reconstructed.
            // Written out per m: an array here was one allocation per splat.
            float t = 1f - (a * a + b * b + c * c);
            float d = (float)Math.Sqrt(Math.Max(0.0, t));
            float w, x, y, z;
            switch (m)
            {
                case 0: w = d; x = a; y = b; z = c; break;
                case 1: w = a; x = d; y = b; z = c; break;
                case 2: w = a; x = b; y = d; z = c; break;
                default: w = a; x = b; y = c; z = d; break;
            }
            rot[off] = x;
            rot[off + 1] = y;
            rot[off + 2] = z;
            rot[off + 3] = w;
        }
    }
}
