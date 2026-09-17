using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VDGS
{
    /// <summary>
    /// meta.json (or a .ply header) without opening the GPU buffers, so the UI can
    /// list a capture that is not spawned.
    /// </summary>
    internal sealed class SplatMetaInfo
    {
        public string Kind;
        public int Splats;
        public string PosFormat;
        public string ScaleFormat;
        public string ColorFormat;
        public string ShFormat;
        public long Bytes;
    }

    internal static class SplatMetaFile
    {
        private class MetaDto
        {
            public int splatCount { get; set; }
            public string posFormat { get; set; }
            public string scaleFormat { get; set; }
            public string colorFormat { get; set; }
            public string shFormat { get; set; }
        }

        internal static SplatMetaInfo Read(string path)
        {
            if (path.EndsWith(".ply", StringComparison.OrdinalIgnoreCase))
                return ReadPly(path);
            if (path.EndsWith(".sog", StringComparison.OrdinalIgnoreCase))
                return ReadSogZip(path);
            return ReadDirectory(path);
        }

        /// <summary>
        /// Classifies a directory's meta.json / lod-meta.json the same way Discover does.
        /// Returns "ssog", "sog", "converted", or null when the folder is not a capture.
        /// </summary>
        internal static string ClassifyDirectory(string dir, out string detail)
        {
            detail = null;
            var lodMeta = Path.Combine(dir, "lod-meta.json");
            if (File.Exists(lodMeta))
                return "ssog";

            var metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath))
                return null;

            try
            {
                var root = JObject.Parse(File.ReadAllText(metaPath));
                if (root["formatVersion"] != null)
                    return "converted";
                if (root.Value<int?>("version") == 2 && root["means"] != null)
                    return "sog";
                detail = "unrecognised meta.json";
                return null;
            }
            catch (Exception e)
            {
                detail = "unreadable meta.json (" + e.Message + ")";
                return null;
            }
        }

        private static SplatMetaInfo ReadDirectory(string dir)
        {
            var kind = ClassifyDirectory(dir, out _);
            if (kind == "ssog")
                return ReadSsog(dir);
            if (kind == "sog")
                return ReadSogDirectory(dir);
            return ReadConverted(dir);
        }

        private static SplatMetaInfo ReadSsog(string dir)
        {
            var info = new SplatMetaInfo { Kind = "ssog" };
            var lodMeta = Path.Combine(dir, "lod-meta.json");
            try
            {
                var root = JObject.Parse(File.ReadAllText(lodMeta));
                info.Splats = root.Value<int?>("count") ?? 0;
            }
            catch { }
            info.Bytes = SumBytes(dir, recursive: true);
            return info;
        }

        private static SplatMetaInfo ReadSogDirectory(string dir)
        {
            var info = new SplatMetaInfo { Kind = "sog" };
            var metaPath = Path.Combine(dir, "meta.json");
            try
            {
                var root = JObject.Parse(File.ReadAllText(metaPath));
                info.Splats = root.Value<int?>("count") ?? 0;
            }
            catch { }
            info.Bytes = SumBytes(dir, recursive: false);
            return info;
        }

        private static SplatMetaInfo ReadSogZip(string path)
        {
            var info = new SplatMetaInfo
            {
                Kind = "sog",
                Bytes = File.Exists(path) ? new FileInfo(path).Length : 0,
            };
            try
            {
                using (var fs = File.OpenRead(path))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false))
                {
                    var entry = zip.GetEntry("meta.json");
                    if (entry == null)
                    {
                        foreach (var e in zip.Entries)
                        {
                            if (string.Equals(e.FullName.Replace('\\', '/'), "meta.json",
                                              StringComparison.Ordinal))
                            {
                                entry = e;
                                break;
                            }
                        }
                    }
                    if (entry == null) return info;
                    using (var s = entry.Open())
                    using (var r = new StreamReader(s, Encoding.UTF8))
                    {
                        var root = JObject.Parse(r.ReadToEnd());
                        info.Splats = root.Value<int?>("count") ?? 0;
                    }
                }
            }
            catch { }
            return info;
        }

        private static SplatMetaInfo ReadConverted(string dir)
        {
            var info = new SplatMetaInfo { Kind = "converted" };
            var metaPath = Path.Combine(dir, "meta.json");
            if (File.Exists(metaPath))
            {
                var dto = JsonConvert.DeserializeObject<MetaDto>(File.ReadAllText(metaPath));
                if (dto != null)
                {
                    info.Splats = dto.splatCount;
                    info.PosFormat = dto.posFormat;
                    info.ScaleFormat = dto.scaleFormat;
                    info.ColorFormat = dto.colorFormat;
                    info.ShFormat = dto.shFormat;
                }
            }

            info.Bytes = SumBytes(dir, recursive: false);
            return info;
        }

        private static long SumBytes(string dir, bool recursive)
        {
            long bytes = 0;
            if (!Directory.Exists(dir)) return 0;
            foreach (var file in Directory.GetFiles(dir))
            {
                if (string.Equals(Path.GetFileName(file), "placement.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                bytes += new FileInfo(file).Length;
            }
            if (recursive)
            {
                foreach (var sub in Directory.GetDirectories(dir))
                    bytes += SumBytes(sub, true);
            }
            return bytes;
        }

        private static SplatMetaInfo ReadPly(string path)
        {
            var info = new SplatMetaInfo
            {
                Kind = "ply",
                Bytes = File.Exists(path) ? new FileInfo(path).Length : 0,
            };
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var buf = new byte[8192];
                    int n = fs.Read(buf, 0, buf.Length);
                    var text = Encoding.ASCII.GetString(buf, 0, n);
                    var i = text.IndexOf("element vertex ", StringComparison.Ordinal);
                    if (i < 0) return info;
                    i += "element vertex ".Length;
                    var end = text.IndexOfAny(new[] { ' ', '\n', '\r' }, i);
                    if (end < 0) end = text.Length;
                    if (int.TryParse(text.Substring(i, end - i), out var c))
                        info.Splats = c;
                }
            }
            catch { }
            return info;
        }
    }
}
