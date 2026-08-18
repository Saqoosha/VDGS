using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// Turns a 3DGS .ply into the buffers the renderer uploads, at load time.
    ///
    /// The point is distribution. Producing a scene currently needs a Python script and a
    /// second Unity install, which is fine for the author and impossible for anyone else.
    /// The ten minutes that conversion takes are k-means clustering of spherical
    /// harmonics and nothing else; a .ply body is a packed array of fixed-size rows, so
    /// everything else is seconds. Measured in the game's own Mono on drjohnson
    /// (3.18M splats, 750 MB): header 1 ms, read 213 ms, decode 2274 ms, sort 407 ms.
    ///
    /// Properties are resolved by NAME, never by offset. Layouts genuinely vary -
    /// drjohnson-aligned.ply carries 59 floats per row because it has no normals, where
    /// the usual export has 62 - and reading fixed offsets silently produces a scene made
    /// of the wrong numbers.
    /// </summary>
    public static class PlyLoader
    {
        // 0.5 + C0 * f_dc is the DC term of the SH basis, i.e. the splat's base colour.
        private const float kSH_C0 = 0.2820948f;
        private static readonly float kSqrt2 = Mathf.Sqrt(2f);

        /// <summary>Reads a .ply. Returns null and fills <paramref name="error"/> on failure.</summary>
        /// <param name="mirrorY">
        /// Reflect across Y. On by default because a capture read straight into Unity is
        /// always mirrored; off is for testing, where it lets this loader be compared
        /// against the offline converter under an identical transform.
        /// </param>
        public static SplatData Load(string path, out string error, bool mirrorY = true)
        {
            error = null;
            try
            {
                return LoadInner(path, ref error, mirrorY);
            }
            catch (Exception e)
            {
                error = "ply load failed: " + e.Message;
                return null;
            }
        }

        private sealed class Header
        {
            public int Count;
            public int Stride;                 // bytes per vertex row
            public int DataStart;              // byte offset of the first row
            public readonly Dictionary<string, int> Offset = new Dictionary<string, int>();

            public bool Has(string name) => Offset.ContainsKey(name);
            public int this[string name] => Offset[name];
        }

        private static Header ReadHeader(Stream fs, ref string error)
        {
            // The header is ASCII and short; the body is binary and huge. Read enough to
            // find end_header without pulling the whole file in.
            var buf = new byte[1 << 16];
            int n = fs.Read(buf, 0, buf.Length);
            var text = System.Text.Encoding.ASCII.GetString(buf, 0, n);
            int end = text.IndexOf("end_header\n", StringComparison.Ordinal);
            if (end < 0)
            {
                error = "no end_header in the first 64 KB - not a .ply?";
                return null;
            }

            var h = new Header { DataStart = end + "end_header\n".Length };
            var lines = text.Substring(0, end).Split('\n');
            bool inVertex = false;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("format ", StringComparison.Ordinal))
                {
                    if (line.IndexOf("binary_little_endian", StringComparison.Ordinal) < 0)
                    {
                        error = "only binary_little_endian .ply is supported, header says: " + line;
                        return null;
                    }
                }
                else if (line.StartsWith("element ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ');
                    inVertex = parts.Length >= 3 && parts[1] == "vertex";
                    if (inVertex) h.Count = int.Parse(parts[2], CultureInfo.InvariantCulture);
                }
                else if (inVertex && line.StartsWith("property ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ');
                    if (parts.Length < 3) continue;
                    if (parts[1] != "float" && parts[1] != "float32")
                    {
                        error = "only float properties are supported, found: " + line;
                        return null;
                    }
                    h.Offset[parts[2]] = h.Stride;
                    h.Stride += 4;
                }
            }

            if (h.Count <= 0) { error = "no vertex element"; return null; }
            foreach (var required in new[] { "x", "y", "z", "opacity",
                                             "scale_0", "scale_1", "scale_2",
                                             "rot_0", "rot_1", "rot_2", "rot_3",
                                             "f_dc_0", "f_dc_1", "f_dc_2" })
            {
                if (!h.Has(required)) { error = "ply is missing property '" + required + "'"; return null; }
            }
            return h;
        }

        private static SplatData LoadInner(string path, ref string error, bool mirrorY)
        {
            // Timings go to the log because this runs while the game is frozen: how long
            // a capture takes to appear is a user-visible number, and which phase owns it
            // decides whether it is worth moving off the main thread.
            var sw = Stopwatch.StartNew();
            double tHeader, tRead;

            byte[] bytes;
            Header h;
            using (var fs = File.OpenRead(path))
            {
                h = ReadHeader(fs, ref error);
                if (h == null) return null;
            }
            tHeader = sw.Elapsed.TotalMilliseconds; sw.Restart();
            bytes = File.ReadAllBytes(path);
            tRead = sw.Elapsed.TotalMilliseconds; sw.Restart();

            long need = (long)h.DataStart + (long)h.Count * h.Stride;
            if (bytes.LongLength < need)
            {
                error = string.Format("ply is truncated: {0} bytes, need {1} for {2} vertices",
                    bytes.LongLength, need, h.Count);
                return null;
            }

            int count = h.Count;
            // SH degree is whatever f_rest_* the file actually carries. luigi has none.
            int shFloats = 0;
            while (h.Has("f_rest_" + shFloats)) shFloats++;
            if (shFloats != 0 && shFloats != 45)
            {
                error = "expected 0 or 45 f_rest_* properties, found " + shFloats;
                return null;
            }

            // Half precision for colour and spherical harmonics, full for geometry.
            //
            // Those two are 208 of the 236 bytes a splat costs at Float32, and halving
            // them needs no chunks: the tighter formats below Float16 store 0..1 weights
            // that only become real values through a chunk's min/max, so they would drag
            // in a Morton sort and per-chunk bounds. Float16 is absolute, so it is a
            // straight swap - 236 bytes per splat down to 132.
            //
            // Colour survives it easily; it is a 0..1 quantity and half gives about three
            // decimal digits. SH coefficients are small and get multiplied by band
            // constants below 3, so they survive too.
            var posData = new byte[count * 12];
            var otherData = new byte[count * 16];      // packed rotation + float3 scale
            // A capture with no f_rest_* needs no SH buffer at all - the shader skips the
            // read when _SplatSHOrder is 0. Allocating it anyway is not a rounding error:
            // nelson-full is 8.76M splats, which is 1.68 GB of zeros, 78% of the largest
            // array the runtime can address, uploaded to the GPU and traversed every frame
            // to be multiplied by nothing.
            var shData = new byte[shFloats > 0 ? count * 96 : 16];   // 45 halves, padded to 16

            SplatData.CalcTextureSize(count, out int texW, out int texH);
            var colorData = new byte[texW * texH * 8];               // half4 per texel

            int oX = h["x"], oY = h["y"], oZ = h["z"];
            int oOpacity = h["opacity"];
            int oS0 = h["scale_0"], oS1 = h["scale_1"], oS2 = h["scale_2"];
            int oR0 = h["rot_0"], oR1 = h["rot_1"], oR2 = h["rot_2"], oR3 = h["rot_3"];
            int oC0 = h["f_dc_0"], oC1 = h["f_dc_1"], oC2 = h["f_dc_2"];
            int oRest = shFloats > 0 ? h["f_rest_0"] : -1;

            // Decoding is 97% of the load - 3.15 s of the 3.25 s a 2.17M capture takes on
            // the RTX 3060 host - and every splat is independent, writing to its own
            // disjoint slice of each buffer. Splitting it across cores is a much smaller
            // change than moving the whole load off the main thread, and it attacks the
            // part that actually costs.
            int workers = Mathf.Clamp(SystemInfo.processorCount, 1, 16);
            int perWorker = (count + workers - 1) / workers;
            var mins = new Vector3[workers];
            var maxs = new Vector3[workers];

            Parallel.For(0, workers, w =>
            {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            int lo = w * perWorker, hi = Mathf.Min(lo + perWorker, count);
            for (int i = lo; i < hi; i++)
            {
                int b = h.DataStart + i * h.Stride;

                // 3DGS is right-handed Y-down, Unity is left-handed Y-up, and nothing in
                // the render path converts between them. Reflecting Y fixes the flip and
                // the handedness together; a 180-degree rotation cannot, because its
                // determinant is +1 and a mirror needs -1.
                float sign = mirrorY ? -1f : 1f;
                float px = F(bytes, b + oX);
                float py = sign * F(bytes, b + oY);
                float pz = F(bytes, b + oZ);
                Put(posData, i * 12, px, py, pz);
                if (px < min.x) min.x = px; if (px > max.x) max.x = px;
                if (py < min.y) min.y = py; if (py > max.y) max.y = py;
                if (pz < min.z) min.z = pz; if (pz > max.z) max.z = pz;

                // Reflecting across Y negates the two quaternion components that are NOT
                // the mirrored axis. Negating w as well looks harmless - q and -q are the
                // same rotation - but that identity needs all four to flip, and getting
                // it wrong leaves positions perfect while every ellipsoid points
                // somewhere else.
                float qw = F(bytes, b + oR0);
                float qx = sign * F(bytes, b + oR1);
                float qy = F(bytes, b + oR2);
                float qz = sign * F(bytes, b + oR3);
                uint packed = PackRotation(qx, qy, qz, qw);
                Put(otherData, i * 16, packed);
                Put(otherData, i * 16 + 4,
                    Mathf.Exp(F(bytes, b + oS0)),
                    Mathf.Exp(F(bytes, b + oS1)),
                    Mathf.Exp(F(bytes, b + oS2)));

                // The colour texture is Morton-swizzled inside 16x16 tiles, matching
                // SplatIndexToPixelIndex in the HLSL. Writing it linearly scrambles
                // colour in 16x16 blocks while leaving the geometry perfect.
                MortonTexel(i, texW, out int tx, out int ty);
                int c = (ty * texW + tx) * 8;
                PutHalf(colorData, c,     0.5f + kSH_C0 * F(bytes, b + oC0));
                PutHalf(colorData, c + 2, 0.5f + kSH_C0 * F(bytes, b + oC1));
                PutHalf(colorData, c + 4, 0.5f + kSH_C0 * F(bytes, b + oC2));
                PutHalf(colorData, c + 6, Sigmoid(F(bytes, b + oOpacity)));

                if (oRest < 0) continue;
                // The .ply groups f_rest by channel - 15 reds, then 15 greens, then 15
                // blues - while the shader reads it coefficient by coefficient as rgb
                // triples. Transpose, or every band comes out in the wrong colour.
                int sh = i * 96;
                for (int k = 0; k < 15; k++)
                {
                    PutHalf(shData, sh + k * 6,     F(bytes, b + oRest + k * 4));
                    PutHalf(shData, sh + k * 6 + 2, F(bytes, b + oRest + (15 + k) * 4));
                    PutHalf(shData, sh + k * 6 + 4, F(bytes, b + oRest + (30 + k) * 4));
                }
            }

            mins[w] = min; maxs[w] = max;
            });

            var minAll = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var maxAll = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int w = 0; w < workers; w++)
            {
                if (w * perWorker >= count) continue;     // a worker with no splats never wrote
                minAll = Vector3.Min(minAll, mins[w]);
                maxAll = Vector3.Max(maxAll, maxs[w]);
            }

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[VDGS] ply '{0}' {1:N0} splats {2:0.0} MB   header {3:0} ms  read {4:0} ms  " +
                "decode {5:0} ms   total {6:0.00} s",
                Path.GetFileNameWithoutExtension(path), count, bytes.LongLength / 1e6,
                tHeader, tRead, sw.Elapsed.TotalMilliseconds,
                (tHeader + tRead + sw.Elapsed.TotalMilliseconds) / 1000.0));

            return SplatData.FromBuffers(
                Path.GetFileNameWithoutExtension(path), count, minAll, maxAll,
                SplatData.VectorFormat.Float32, SplatData.VectorFormat.Float32,
                SplatData.ColorFormat.Float16x4, SplatData.SHFormat.Float16,
                posData, otherData, colorData, shData, null,
                shFloats > 0 ? 3 : 0);
        }

        private static float F(byte[] b, int off) => BitConverter.ToSingle(b, off);
        private static float Sigmoid(float v) => 1f / (1f + Mathf.Exp(-v));

        /// <summary>
        /// Reinterpret a float's bits without allocating.
        ///
        /// BitConverter.GetBytes returns a fresh byte[4], and this writes about ten floats
        /// per splat - twenty million allocations for a two-million-splat capture. That is
        /// slow single-threaded and worse across cores, where the threads end up fighting
        /// over the allocator instead of decoding: splitting the loop across cores made it
        /// twice as slow until this was fixed.
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public uint U;
        }

        private static void Put(byte[] dst, int off, float a)
        {
            var b = new FloatBits { F = a };
            Put(dst, off, b.U);
        }

        private static void Put(byte[] dst, int off, float a, float b, float c)
        {
            Put(dst, off, a);
            Put(dst, off + 4, b);
            Put(dst, off + 8, c);
        }

        private static void Put(byte[] dst, int off, uint v)
        {
            dst[off] = (byte)v;
            dst[off + 1] = (byte)(v >> 8);
            dst[off + 2] = (byte)(v >> 16);
            dst[off + 3] = (byte)(v >> 24);
        }

        private static void PutHalf(byte[] dst, int off, float v)
        {
            ushort h = Mathf.FloatToHalf(v);
            dst[off] = (byte)h;
            dst[off + 1] = (byte)(h >> 8);
        }

        /// <summary>
        /// "Smallest three" 10.10.10.2, the inverse of DecodeRotation in the HLSL.
        ///
        /// The largest component is dropped and rebuilt from the unit-length constraint,
        /// so the quaternion is negated first when that component is negative - q and -q
        /// are the same rotation, and the decoder always rebuilds a positive one.
        /// </summary>
        private static uint PackRotation(float x, float y, float z, float w)
        {
            float len = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (len > 1e-20f) { float inv = 1f / len; x *= inv; y *= inv; z *= inv; w *= inv; }
            else { x = y = z = 0f; w = 1f; }

            // Order here is (x,y,z,w), matching the decoder's float4 - not the .ply's
            // (w,x,y,z).
            var q = new[] { x, y, z, w };
            int largest = 0;
            for (int i = 1; i < 4; i++)
                if (Mathf.Abs(q[i]) > Mathf.Abs(q[largest])) largest = i;
            if (q[largest] < 0f)
                for (int i = 0; i < 4; i++) q[i] = -q[i];

            uint bits = (uint)largest << 30;
            int slot = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest) continue;
                // Decode is v * sqrt(2) - 1/sqrt(2), so encode is (v + 1/sqrt(2)) / sqrt(2).
                float stored = (q[i] + 1f / kSqrt2) / kSqrt2;
                uint u = (uint)Mathf.Clamp(Mathf.RoundToInt(stored * 1023f), 0, 1023);
                bits |= u << (slot * 10);
                slot++;
            }
            return bits;
        }

        /// <summary>Splat index to texel, matching SplatIndexToPixelIndex in the HLSL.</summary>
        private static void MortonTexel(int idx, int texWidth, out int x, out int y)
        {
            uint t = (uint)idx & 0xFF;
            t = (t & 0xFF) | ((t & 0xFE) << 7);
            t &= 0x5555;
            t = (t ^ (t >> 1)) & 0x3333;
            t = (t ^ (t >> 2)) & 0x0f0f;
            int mx = (int)(t & 0xF), my = (int)(t >> 8);

            int tilesPerRow = texWidth / 16;
            int tile = idx >> 8;
            x = (tile % tilesPerRow) * 16 + mx;
            y = (tile / tilesPerRow) * 16 + my;
        }
    }
}
