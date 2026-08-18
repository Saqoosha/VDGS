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
            // -1 means the file predates this field, so the count is unknown.
            public int chunkCount = -1;
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

        /// <summary>
        /// Highest spherical-harmonics band this capture actually carries. Handheld LiDAR
        /// scanners routinely emit degree 0, and .ply files from them have no f_rest_*
        /// at all. The renderer clamps its own SH order to this, which lets a scene with
        /// no harmonics skip both the buffer and the per-frame read.
        /// </summary>
        public int ShOrder { get; private set; } = 3;

        public byte[] ChunkData { get; private set; }
        public byte[] PosData { get; private set; }
        public byte[] OtherData { get; private set; }
        public byte[] ColorData { get; private set; }
        public byte[] ShData { get; private set; }

        public bool HasChunks => ChunkData != null && ChunkData.Length > 0;

        /// <summary>
        /// Build a scene from buffers produced in memory rather than read from disk.
        ///
        /// Used by PlyLoader, so a capture can be dropped in as a .ply and converted at
        /// load time instead of going through a Python script and a second Unity install
        /// that only the author can run.
        /// </summary>
        internal static SplatData FromBuffers(
            string name, int count, Vector3 boundsMin, Vector3 boundsMax,
            VectorFormat pos, VectorFormat scale, ColorFormat color, SHFormat sh,
            byte[] posData, byte[] otherData, byte[] colorData, byte[] shData, byte[] chunkData,
            int shOrder = 3)
        {
            return new SplatData
            {
                Name = name,
                SplatCount = count,
                BoundsMin = boundsMin,
                BoundsMax = boundsMax,
                PosFormat = pos,
                ScaleFormat = scale,
                ColorFmt = color,
                ShFormat = sh,
                PosData = posData,
                OtherData = otherData,
                ColorData = colorData,
                ShData = shData,
                ChunkData = chunkData,
                ShOrder = shOrder,
            };
        }

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
            if (!AcceptChunks(d, dir, ref error, meta.chunkCount)) return null;
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
        /// Chunked data stores positions, scales and colours as 0..1 weights inside each
        /// chunk's box, and the shader turns them back into world values only when a
        /// chunk buffer is bound:
        ///
        ///     if (chunkIdx &lt; _SplatChunkCount)
        ///         pos = lerp(chunk.posMin, chunk.posMax, pos);
        ///
        /// So a chunk.bin that does not belong to this data is catastrophic in both
        /// directions, and silent in both. A leftover one applied to absolute positions
        /// extrapolates the scene off into space and raises scale to the eighth power -
        /// that is the "scattered debris" this cost a day to find. A missing one leaves
        /// every splat at its 0..1 weight, collapsing the whole capture into a blob near
        /// the origin.
        ///
        /// **posFormat does not answer this.** "Float32" is the storage width, not the
        /// coordinate space: a chunked scene stores 0..1 weights in Float32 quite
        /// happily, which a first attempt at this guard assumed away and broke every
        /// chunked capture. Only the conversion knows, so it writes chunkCount into
        /// meta.json and this compares against it.
        /// </summary>
        private static bool AcceptChunks(SplatData d, string dir, ref string error, int declared)
        {
            var have = d.ChunkData == null ? 0 : d.ChunkData.Length / SplatRenderer.ChunkInfo.kSize;

            if (declared < 0)
            {
                // Written before chunkCount existed. Fall back to the arithmetic: a
                // chunk file that does not cover the splats cannot be this scene's.
                var expected = (d.SplatCount + kChunkSize - 1) / kChunkSize;
                if (have != 0 && have != expected)
                {
                    Debug.LogWarning("[VDGS] " + new DirectoryInfo(dir).Name +
                        ": ignoring chunk.bin with " + have + " chunks; " + expected +
                        " would be needed for " + d.SplatCount + " splats");
                    d.ChunkData = null;
                }
                return true;
            }

            if (have != declared)
            {
                error = "chunk.bin holds " + have + " chunks but meta.json declares " +
                        declared + (declared == 0
                            ? " - delete the leftover file (the deploy should have)"
                            : " - the file is stale or truncated");
                return false;
            }

            if (have == 0) d.ChunkData = null;
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
