using System;
using System.IO;
using VDGS.Sog;
using Xunit;

public class SogSourceTests
{
    // Every name that reaches Resolve was written by whoever made the capture, and a capture
    // is something a player downloads from a public site.
    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("0_0/../../secret.txt")]
    [InlineData("")]
    [InlineData("0_0//meta.json")]
    public void RefusesNamesThatLeaveTheCapture(string name)
    {
        var dir = NewCapture(out var _);
        try
        {
            var e = Assert.Throws<SogException>(() => SogSource.Resolve(dir, name));
            Assert.Contains("capture", e.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RefusesAnAbsoluteName()
    {
        var dir = NewCapture(out var outside);
        try
        {
            Assert.Throws<SogException>(() => SogSource.Resolve(dir, outside.Replace('\\', '/')));
        }
        finally { Directory.Delete(dir, true); }
    }

    // "./" is what some writers emit, and the zip reader drops it too — the two must agree,
    // or the same capture loads bundled and fails unpacked.
    [Theory]
    [InlineData("0_0/meta.json")]
    [InlineData("./0_0/meta.json")]
    public void ResolvesANameInsideTheCapture(string name)
    {
        var dir = NewCapture(out var _);
        try
        {
            var path = SogSource.Resolve(dir, name);
            Assert.Equal("{}", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SaysSoWhenTheNameIsInsideButMissing()
    {
        var dir = NewCapture(out var _);
        try
        {
            var e = Assert.Throws<SogException>(() => SogSource.Resolve(dir, "0_0/quats.webp"));
            Assert.Contains("missing", e.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A capture directory holding 0_0/meta.json, plus a file outside it to aim at.</summary>
    private static string NewCapture(out string outsideFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "vdgs-sog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "capture", "0_0"));
        File.WriteAllText(Path.Combine(root, "capture", "0_0", "meta.json"), "{}");
        outsideFile = Path.Combine(root, "secret.txt");
        File.WriteAllText(outsideFile, "not yours");
        return Path.Combine(root, "capture");
    }

    // A zip entry's declared length is the archive's claim; the reader counts what inflates.
    [Fact]
    public void ReadCappedStopsAtTheCap()
    {
        Assert.Equal(10, SogSource.ReadCapped(new MemoryStream(new byte[10]), 10, "x").Length);
        var e = Assert.Throws<SogException>(() => SogSource.ReadCapped(new MemoryStream(new byte[11]), 10, "x"));
        Assert.Contains("exceeds 10 bytes", e.Message);
    }
}
