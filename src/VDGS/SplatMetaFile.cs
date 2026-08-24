using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

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
            return ReadConverted(path);
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

            long bytes = 0;
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    if (string.Equals(Path.GetFileName(file), "placement.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    bytes += new FileInfo(file).Length;
                }
            }
            info.Bytes = bytes;
            return info;
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
