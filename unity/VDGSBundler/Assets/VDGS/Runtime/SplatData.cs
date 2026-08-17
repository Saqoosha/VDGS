using System;
using System.IO;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// A Gaussian Splat scene loaded from plain files instead of a ScriptableObject.
    ///
    /// Upstream stores this as a GaussianSplatAsset holding five TextAssets. Inside an
    /// injected plugin there is no AssetDatabase and a ScriptableObject coming out of an
    /// AssetBundle would fail type resolution against the game's assemblies, so the same
    /// bytes are read straight off disk and the metadata comes from meta.json.
    ///
    /// Layout on disk (see AGENTS.md):
    ///   &lt;game&gt;/vdgs/&lt;name&gt;/{meta.json, chunk.bin, pos.bin, other.bin, color.bin, sh.bin}
    /// </summary>
    public class SplatData
    {
        // Must match GaussianSplatAsset.kCurrentVersion upstream.
        public const int kCurrentVersion = 2023_10_20;
        public const int kChunkSize = 256;
        public const int kTextureWidth = 2048;

        // These mirror VECTOR_FMT_* / COLOR_FMT_* / SH_FMT_* in the HLSL. The numeric
        // values are passed to the compute shader, so the order must not change.
        public enum VectorFormat { Float32 = 0, Norm16 = 1, Norm11 = 2, Norm6 = 3 }
        public enum ColorFormat { Float32x4 = 0, Float16x4 = 1, Norm8x4 = 2, BC7 = 3 }
        public enum SHFormat
        {
            Float32 = 0, Float16 = 1, Norm11 = 2, Norm6 = 3,
            Cluster64k = 4, Cluster32k = 5, Cluster16k = 6, Cluster8k = 7, Cluster4k = 8
        }

        [Serializable]
        private class Meta
        {
            public int formatVersion;
            public int splatCount;
            public float[] boundsMin;
            public float[] boundsMax;
            public string posFormat;
            public string scaleFormat;
            public string colorFormat;
            public string shFormat;
        }

        public string Name { get; private set; }
        public int SplatCount { get; private set; }
        public Vector3 BoundsMin { get; private set; }
        public Vector3 BoundsMax { get; private set; }
        public VectorFormat PosFormat { get; private set; }
        public VectorFormat ScaleFormat { get; private set; }
        public ColorFormat ColorFmt { get; private set; }
        public SHFormat ShFormat { get; private set; }

        public byte[] ChunkData { get; private set; }
        public byte[] PosData { get; private set; }
        public byte[] OtherData { get; private set; }
        public byte[] ColorData { get; private set; }
        public byte[] ShData { get; private set; }

        public bool HasChunks => ChunkData != null && ChunkData.Length > 0;

        /// <summary>Loads a splat scene directory. Returns null and fills <paramref name="error"/> on failure.</summary>
        public static SplatData Load(string dir, out string error)
        {
            error = null;

            var metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath))
            {
                error = "meta.json not found in " + dir;
                return null;
            }

            Meta meta;
            try
            {
                meta = JsonUtility.FromJson<Meta>(File.ReadAllText(metaPath));
            }
            catch (Exception e)
            {
                error = "meta.json parse failed: " + e.Message;
                return null;
            }

            if (meta == null)
            {
                error = "meta.json produced no object";
                return null;
            }
            if (meta.formatVersion != kCurrentVersion)
            {
                error = "formatVersion " + meta.formatVersion + " != expected " + kCurrentVersion;
                return null;
            }
            if (meta.splatCount <= 0)
            {
                error = "splatCount is " + meta.splatCount;
                return null;
            }

            var d = new SplatData
            {
                Name = new DirectoryInfo(dir).Name,
                SplatCount = meta.splatCount,
                BoundsMin = ToVec(meta.boundsMin),
                BoundsMax = ToVec(meta.boundsMax),
            };

            if (!ParseEnum(meta.posFormat, out VectorFormat pos, ref error)) return null;
            if (!ParseEnum(meta.scaleFormat, out VectorFormat scale, ref error)) return null;
            if (!ParseEnum(meta.colorFormat, out ColorFormat color, ref error)) return null;
            if (!ParseEnum(meta.shFormat, out SHFormat sh, ref error)) return null;
            d.PosFormat = pos;
            d.ScaleFormat = scale;
            d.ColorFmt = color;
            d.ShFormat = sh;

            // chunk.bin is optional: a scene built without chunking has none.
            d.ChunkData = ReadOptional(Path.Combine(dir, "chunk.bin"));
            if (!AcceptChunks(d, dir, ref error)) return null;
            if (!ReadRequired(dir, "pos.bin", out var posBytes, ref error)) return null;
            if (!ReadRequired(dir, "other.bin", out var otherBytes, ref error)) return null;
            if (!ReadRequired(dir, "color.bin", out var colorBytes, ref error)) return null;
            if (!ReadRequired(dir, "sh.bin", out var shBytes, ref error)) return null;
            d.PosData = posBytes;
            d.OtherData = otherBytes;
            d.ColorData = colorBytes;
            d.ShData = shBytes;

            return d;
        }

        /// <summary>
        /// Decide whether chunk.bin may be used, and refuse it when it cannot be trusted.
        ///
        /// This matters far more than it looks. The shader applies chunk data purely on
        /// "is a chunk buffer bound", with no check of the position format:
        ///
        ///     if (chunkIdx &lt; _SplatChunkCount)
        ///         pos = lerp(chunk.posMin, chunk.posMax, pos);
        ///
        /// With chunked data `pos` is a 0..1 weight inside the chunk box, so that is
        /// correct. With Float32 data `pos` is already an absolute coordinate, and
        /// feeding -23.2 to a lerp extrapolates it out of the world. Scale is worse:
        /// it is lerped and then raised to the eighth power.
        ///
        /// So a stale chunk.bin left behind by an earlier, chunked conversion turns a
        /// perfectly good scene into scattered debris - and nothing errors, because the
        /// file is well formed. That is exactly what happened after switching to
        /// VeryHigh: the deploy overwrote pos/other/color/sh and left chunk.bin in place.
        /// A whole day went into suspecting the quaternions instead.
        /// </summary>
        private static bool AcceptChunks(SplatData d, string dir, ref string error)
        {
            if (d.ChunkData == null || d.ChunkData.Length == 0)
            {
                d.ChunkData = null;
                return true;
            }

            if (d.PosFormat == VectorFormat.Float32)
            {
                // Float32 positions are absolute, so chunks cannot apply. The file is a
                // leftover; dropping it is right, but say so loudly - a scene silently
                // shedding a file it shipped with is worth knowing about.
                Debug.LogWarning("[VDGS] " + new DirectoryInfo(dir).Name +
                    ": ignoring a stale chunk.bin (" + d.ChunkData.Length +
                    " bytes) - posFormat is Float32, which stores absolute positions. " +
                    "Delete it; the deploy should have.");
                d.ChunkData = null;
                return true;
            }

            int expected = (d.SplatCount + kChunkSize - 1) / kChunkSize;
            int actual = d.ChunkData.Length / SplatRenderer.ChunkInfo.kSize;
            if (actual != expected)
            {
                error = "chunk.bin holds " + actual + " chunks, expected " + expected +
                        " for " + d.SplatCount + " splats - it is stale or truncated";
                return false;
            }

            return true;
        }

        /// <summary>Colour data is uploaded as a texture; upstream fixes the width at 2048.</summary>
        public static void CalcTextureSize(int splatCount, out int width, out int height)
        {
            width = kTextureWidth;
            height = (splatCount + width - 1) / width;
            // Round up to a multiple of the chunk size so partial chunks stay addressable.
            height = (height + 15) / 16 * 16;
        }

        public string Describe()
        {
            return string.Format(
                "'{0}' splats={1} pos={2} scale={3} color={4} sh={5} bounds=({6})..({7}) " +
                "bytes chunk={8} pos={9} other={10} color={11} sh={12}",
                Name, SplatCount, PosFormat, ScaleFormat, ColorFmt, ShFormat,
                BoundsMin, BoundsMax,
                ChunkData == null ? 0 : ChunkData.Length,
                PosData.Length, OtherData.Length, ColorData.Length, ShData.Length);
        }

        private static Vector3 ToVec(float[] v)
        {
            if (v == null || v.Length < 3) return Vector3.zero;
            return new Vector3(v[0], v[1], v[2]);
        }

        private static bool ParseEnum<T>(string s, out T value, ref string error) where T : struct
        {
            value = default;
            if (string.IsNullOrEmpty(s))
            {
                error = "missing " + typeof(T).Name + " in meta.json";
                return false;
            }
            try
            {
                value = (T)Enum.Parse(typeof(T), s, true);
                return true;
            }
            catch
            {
                error = "unknown " + typeof(T).Name + ": '" + s + "'";
                return false;
            }
        }

        private static byte[] ReadOptional(string path)
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        private static bool ReadRequired(string dir, string file, out byte[] bytes, ref string error)
        {
            bytes = null;
            var path = Path.Combine(dir, file);
            if (!File.Exists(path))
            {
                error = file + " not found in " + dir;
                return false;
            }
            bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                error = file + " is empty";
                return false;
            }
            return true;
        }
    }
}
