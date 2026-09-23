using System.Runtime.InteropServices;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// Packs splats into the resident layout PlyLoader has always produced.
    /// Thread-safe for disjoint splat indices.
    /// </summary>
    internal sealed class SplatWriter
    {
        private static readonly float kSqrt2 = Mathf.Sqrt(2f);

        private readonly int m_Count;
        private readonly int m_OtherStride;
        private readonly bool m_ClusterSh;
        private readonly int m_TexWidth;

        public byte[] Pos { get; }
        public byte[] Other { get; }
        public byte[] Color { get; }
        /// <summary>Null when clusterSh — caller supplies the concatenated palette.</summary>
        public byte[] Sh { get; }
        public int TexWidth => m_TexWidth;
        public int TexHeight { get; }

        public SplatWriter(int count, bool withShFloat16, bool clusterSh)
        {
            m_Count = count;
            m_ClusterSh = clusterSh;
            m_OtherStride = clusterSh ? 18 : 16;

            Pos = new byte[count * 12];
            // Whole words: RawBuffer drops a partial last word (an odd count at stride 18).
            Other = new byte[(count * m_OtherStride + 3) & ~3];

            SplatData.CalcTextureSize(count, out int texW, out int texH);
            m_TexWidth = texW;
            TexHeight = texH;
            Color = new byte[texW * texH * 8];

            if (clusterSh)
                Sh = null;
            else if (withShFloat16)
                Sh = new byte[count * 96];
            else
                Sh = new byte[16];
        }

        /// <summary>
        /// rot is x y z w; opacity 0..1; colour already in display space (e.g. 0.5 + C0 * dc);
        /// scale is linear (exp already applied).
        /// </summary>
        public void Put(int i, float px, float py, float pz, float qx, float qy, float qz, float qw,
                        float sx, float sy, float sz, float r, float g, float b, float opacity)
        {
            PutFloat3(Pos, i * 12, px, py, pz);

            int o = i * m_OtherStride;
            PutUInt(Other, o, PackRotation(qx, qy, qz, qw));
            PutFloat3(Other, o + 4, sx, sy, sz);

            MortonTexel(i, m_TexWidth, out int tx, out int ty);
            int c = (ty * m_TexWidth + tx) * 8;
            PutHalf(Color, c, r);
            PutHalf(Color, c + 2, g);
            PutHalf(Color, c + 4, b);
            PutHalf(Color, c + 6, opacity);
        }

        public void PutShFloat16(int i, int k, float r, float g, float b)
        {
            int sh = i * 96 + k * 6;
            PutHalf(Sh, sh, r);
            PutHalf(Sh, sh + 2, g);
            PutHalf(Sh, sh + 4, b);
        }

        public void PutShIndex(int i, ushort index)
        {
            int o = i * m_OtherStride + 16;
            Other[o] = (byte)index;
            Other[o + 1] = (byte)(index >> 8);
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public uint U;
        }

        private static void PutFloat(byte[] dst, int off, float a)
        {
            var b = new FloatBits { F = a };
            PutUInt(dst, off, b.U);
        }

        private static void PutFloat3(byte[] dst, int off, float a, float b, float c)
        {
            PutFloat(dst, off, a);
            PutFloat(dst, off + 4, b);
            PutFloat(dst, off + 8, c);
        }

        private static void PutUInt(byte[] dst, int off, uint v)
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
        /// </summary>
        private static uint PackRotation(float x, float y, float z, float w)
        {
            float len = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (len > 1e-20f) { float inv = 1f / len; x *= inv; y *= inv; z *= inv; w *= inv; }
            else { x = y = z = 0f; w = 1f; }

            // Scalars, not an array: this runs once per splat on every loader thread.
            int largest = 0;
            float big = Mathf.Abs(x);
            if (Mathf.Abs(y) > big) { largest = 1; big = Mathf.Abs(y); }
            if (Mathf.Abs(z) > big) { largest = 2; big = Mathf.Abs(z); }
            if (Mathf.Abs(w) > big) largest = 3;
            float lv = largest == 0 ? x : largest == 1 ? y : largest == 2 ? z : w;
            if (lv < 0f) { x = -x; y = -y; z = -z; w = -w; }

            uint bits = (uint)largest << 30;
            int slot = 0;
            if (largest != 0) bits |= Smallest3(x) << (10 * slot++);
            if (largest != 1) bits |= Smallest3(y) << (10 * slot++);
            if (largest != 2) bits |= Smallest3(z) << (10 * slot++);
            if (largest != 3) bits |= Smallest3(w) << (10 * slot++);
            return bits;
        }

        private static uint Smallest3(float v)
        {
            float stored = (v + 1f / kSqrt2) / kSqrt2;
            return (uint)Mathf.Clamp(Mathf.RoundToInt(stored * 1023f), 0, 1023);
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
