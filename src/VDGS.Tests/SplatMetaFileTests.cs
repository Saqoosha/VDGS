using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Xunit;

namespace VDGS.Tests
{
    public class SplatMetaFileTests : IDisposable
    {
        private readonly string _dir;

        public SplatMetaFileTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vdgs-meta-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Converted_reads_formats_and_skips_placement()
        {
            var scene = Path.Combine(_dir, "drjohnson");
            Directory.CreateDirectory(scene);
            var meta = "{\"formatVersion\":20231020,\"splatCount\":3177554,"
                       + "\"posFormat\":\"Norm16\",\"scaleFormat\":\"Norm16\","
                       + "\"colorFormat\":\"Float16x4\",\"shFormat\":\"Norm11\"}";
            File.WriteAllText(Path.Combine(scene, "meta.json"), meta);
            File.WriteAllBytes(Path.Combine(scene, "pos.bin"), new byte[10]);
            File.WriteAllText(Path.Combine(scene, "placement.json"), "{}");

            var info = SplatMetaFile.Read(scene);
            Assert.Equal("converted", info.Kind);
            Assert.Equal(3177554, info.Splats);
            Assert.Equal("Norm16", info.PosFormat);
            Assert.Equal("Norm16", info.ScaleFormat);
            Assert.Equal("Float16x4", info.ColorFormat);
            Assert.Equal("Norm11", info.ShFormat);
            Assert.Equal(new FileInfo(Path.Combine(scene, "meta.json")).Length + 10, info.Bytes);
        }

        [Fact]
        public void Ply_is_kind_ply_with_empty_formats()
        {
            var ply = Path.Combine(_dir, "luigi.ply");
            File.WriteAllText(ply, "ply\nformat binary_little_endian 1.0\nelement vertex 14526\nend_header\nxxxx");
            var info = SplatMetaFile.Read(ply);
            Assert.Equal("ply", info.Kind);
            Assert.Equal(14526, info.Splats);
            Assert.Null(info.PosFormat);
            Assert.Equal(new FileInfo(ply).Length, info.Bytes);
        }

        [Fact]
        public void ReadsStreamedSogCountAndKind()
        {
            var dir = Path.Combine(Path.GetTempPath(), "vdgs-ssog-" + Guid.NewGuid());
            Directory.CreateDirectory(Path.Combine(dir, "0_0"));
            File.WriteAllText(Path.Combine(dir, "lod-meta.json"),
                "{\"version\":1,\"count\":1234,\"counts\":[1000,234],\"lodLevels\":2,"
                + "\"filenames\":[\"0_0/meta.json\"],"
                + "\"tree\":{\"bound\":{\"min\":[0,0,0],\"max\":[1,1,1]},\"lods\":{}}}");
            File.WriteAllBytes(Path.Combine(dir, "0_0", "means_l.webp"), new byte[100]);
            try
            {
                var info = SplatMetaFile.Read(dir);
                Assert.Equal("ssog", info.Kind);
                Assert.Equal(1234, info.Splats);
                Assert.True(info.Bytes >= 100);
            }
            finally { Directory.Delete(dir, true); }
        }

        // The loader already found "./meta.json"; the listing used to miss it and show 0 splats.
        [Theory]
        [InlineData("meta.json")]
        [InlineData("./meta.json")]
        [InlineData(".\\meta.json")]
        public void Bundled_sog_counts_whatever_the_entry_spelling(string entry)
        {
            var path = Path.Combine(_dir, "x.sog");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry(entry).Open()))
                w.Write("{\"version\":2,\"count\":1234}");
            var info = SplatMetaFile.Read(path);
            Assert.Equal("sog", info.Kind);
            Assert.Equal(1234, info.Splats);
        }
    }
}
