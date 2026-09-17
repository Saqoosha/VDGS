using System.IO;
using System.Linq;
using VDGS.Sog;
using Xunit;

public class SsogIndexTests
{
    private static string Ssog => Path.Combine(System.AppContext.BaseDirectory, "fixtures", "sog", "ssog");

    [Fact]
    public void RunsAddUpToTheManifestCounts()
    {
        var idx = SsogIndex.Parse(File.ReadAllBytes(Path.Combine(Ssog, "lod-meta.json")));
        Assert.Equal(2, idx.LevelCount);
        Assert.True(idx.Leaves.Length >= 2);
        for (int lv = 0; lv < idx.LevelCount; lv++)
            Assert.Equal(idx.Counts[lv], idx.Runs.Where(r => r.Level == lv).Sum(r => r.Count));
        Assert.Equal(idx.Count, idx.Runs.Sum(r => r.Count));
    }

    // "A chunk file's contents are exactly the concatenation of the leaf runs that
    // reference it" - so each file's runs tile [0, count) with no gap and no overlap.
    [Fact]
    public void RunsTileEachChunkFile()
    {
        var idx = SsogIndex.Parse(File.ReadAllBytes(Path.Combine(Ssog, "lod-meta.json")));
        using var files = SogSource.OpenDirectory(Ssog);
        for (int f = 0; f < idx.Files.Length; f++)
        {
            var runs = idx.Runs.Where(r => r.File == f).OrderBy(r => r.Offset).ToArray();
            int at = 0;
            foreach (var r in runs) { Assert.Equal(at, r.Offset); at += r.Count; }
            Assert.Equal(SogChunk.Decode(files, idx.Files[f]).Count, at);
        }
    }

    [Fact]
    public void DecodesEveryChunkOfTheStreamedFixture()
    {
        var idx = SsogIndex.Parse(File.ReadAllBytes(Path.Combine(Ssog, "lod-meta.json")));
        using var files = SogSource.OpenDirectory(Ssog);
        Assert.Equal(idx.Count, idx.Files.Sum(f => SogChunk.Decode(files, f).Count));
    }
}
