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

    // The meta must carry the version, and the message must name it: without both, this
    // passes on the next check down (means.files) even if the version guard is deleted.
    [Fact]
    public void RejectsVersionOne()
    {
        using var files = new MemoryFiles("meta.json",
            "{\"version\":1,\"means\":{\"shape\":[1,3]}}");
        var e = Assert.Throws<SogException>(() => SogChunk.Decode(files, "meta.json"));
        Assert.Contains("unsupported SOG version: 1", e.Message);
    }

    private sealed class MemoryFiles : ISogFiles
    {
        private readonly string m_Name; private readonly byte[] m_Bytes;
        public MemoryFiles(string name, string text) { m_Name = name; m_Bytes = System.Text.Encoding.UTF8.GetBytes(text); }
        public byte[] Read(string p) => p == m_Name ? m_Bytes : throw new FileNotFoundException(p);
        public void Dispose() { }
    }

    // The fixtures only carry tag 252, so the other three dropped-component cases are pinned
    // here against the table-driven code they replaced, bit for bit.
    [Fact]
    public void UnpackQuatMatchesTheTableForEveryTag()
    {
        int[] quatIdx = { 1, 2, 3, 0, 2, 3, 0, 1, 3, 0, 1, 2 };
        float invSqrt2 = 1f / (float)Math.Sqrt(2.0);
        var got = new float[4];
        for (int tag = 0; tag < 256; tag++)
        for (int px = 0; px < 256; px += 17)
        for (int py = 0; py < 256; py += 17)
        for (int pz = 0; pz < 256; pz += 17)
        {
            float[] want;
            if (tag < 252) want = new[] { 0f, 0f, 0f, 1f };
            else
            {
                int m = tag - 252;
                float a = (px / 255f * 2f - 1f) * invSqrt2;
                float b = (py / 255f * 2f - 1f) * invSqrt2;
                float c = (pz / 255f * 2f - 1f) * invSqrt2;
                var wxyz = new float[4];
                wxyz[quatIdx[m * 3]] = a;
                wxyz[quatIdx[m * 3 + 1]] = b;
                wxyz[quatIdx[m * 3 + 2]] = c;
                wxyz[m] = (float)Math.Sqrt(Math.Max(0.0, 1f - (a * a + b * b + c * c)));
                want = new[] { wxyz[1], wxyz[2], wxyz[3], wxyz[0] };
            }
            SogChunk.UnpackQuat((byte)px, (byte)py, (byte)pz, (byte)tag, got, 0);
            for (int k = 0; k < 4; k++)
                Assert.Equal(BitConverter.SingleToInt32Bits(want[k]), BitConverter.SingleToInt32Bits(got[k]));
        }
    }
}
