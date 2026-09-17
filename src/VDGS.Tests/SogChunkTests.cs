using System;
using System.IO;
using VDGS.Sog;
using Xunit;

public class SogChunkTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures", "sog");

    // splat-transform's own decode of sh.sog, read back by name. The .sog is lossy, so the
    // reference is not sh.ply but what the reference decoder made of the same bytes.
    private sealed class Ply
    {
        public int Count; public System.Collections.Generic.Dictionary<string, float[]> Col = new();
        public static Ply Read(string path)
        {
            var d = File.ReadAllBytes(path);
            var text = System.Text.Encoding.ASCII.GetString(d, 0, Math.Min(d.Length, 65536));
            int end = text.IndexOf("end_header\n", StringComparison.Ordinal) + 11;
            var names = new System.Collections.Generic.List<string>(); var p = new Ply();
            foreach (var l in text.Substring(0, end).Split('\n'))
            {
                var t = l.Split(' ');
                if (t[0] == "element" && t[1] == "vertex") p.Count = int.Parse(t[2]);
                if (t[0] == "property") { if (t[1] != "float") throw new Exception("float only"); names.Add(t[2]); }
            }
            foreach (var n in names) p.Col[n] = new float[p.Count];
            for (int i = 0; i < p.Count; i++)
                for (int c = 0; c < names.Count; c++)
                    p.Col[names[c]][i] = BitConverter.ToSingle(d, end + (i * names.Count + c) * 4);
            return p;
        }
    }

    [Fact]
    public void BundledSogMatchesReferenceDecoder()
    {
        using var files = SogSource.OpenZip(Path.Combine(Dir, "sh.sog"));
        var s = SogChunk.Decode(files, "meta.json");
        var r = Ply.Read(Path.Combine(Dir, "sh-decoded.ply"));
        Assert.Equal(r.Count, s.Count);
        Assert.Equal(3, s.ShBands);
        for (int i = 0; i < s.Count; i++)
        {
            Assert.Equal(r.Col["x"][i], s.Pos[i * 3], 4);
            Assert.Equal(r.Col["y"][i], s.Pos[i * 3 + 1], 4);
            Assert.Equal(r.Col["z"][i], s.Pos[i * 3 + 2], 4);
            Assert.Equal(MathF.Exp(r.Col["scale_0"][i]), s.Scale[i * 3], 4);
            // ply rot_0..3 is w x y z; ours is x y z w
            Assert.Equal(r.Col["rot_1"][i], s.Rot[i * 4], 4);
            Assert.Equal(r.Col["rot_2"][i], s.Rot[i * 4 + 1], 4);
            Assert.Equal(r.Col["rot_3"][i], s.Rot[i * 4 + 2], 4);
            Assert.Equal(r.Col["rot_0"][i], s.Rot[i * 4 + 3], 4);
            Assert.Equal(0.5f + 0.2820948f * r.Col["f_dc_1"][i], s.Color[i * 3 + 1], 4);
            Assert.Equal(1f / (1f + MathF.Exp(-r.Col["opacity"][i])), s.Opacity[i], 3);
            int row = s.ShLabel[i] * 45;
            for (int k = 0; k < 15; k++)
            {
                // ply f_rest is channel-major: 15 reds, 15 greens, 15 blues
                Assert.Equal(r.Col["f_rest_" + k][i], s.ShPalette[row + k * 3], 4);
                Assert.Equal(r.Col["f_rest_" + (15 + k)][i], s.ShPalette[row + k * 3 + 1], 4);
                Assert.Equal(r.Col["f_rest_" + (30 + k)][i], s.ShPalette[row + k * 3 + 2], 4);
            }
        }
    }

    [Fact]
    public void RejectsVersionOne()
    {
        using var files = new MemoryFiles("meta.json", "{\"means\":{\"shape\":[1,3]}}");
        Assert.Throws<SogException>(() => SogChunk.Decode(files, "meta.json"));
    }

    private sealed class MemoryFiles : ISogFiles
    {
        private readonly string m_Name; private readonly byte[] m_Bytes;
        public MemoryFiles(string name, string text) { m_Name = name; m_Bytes = System.Text.Encoding.UTF8.GetBytes(text); }
        public byte[] Read(string p) => p == m_Name ? m_Bytes : throw new FileNotFoundException(p);
        public void Dispose() { }
    }
}
