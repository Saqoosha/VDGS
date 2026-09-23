using System.IO;
using System.Linq;
using VDGS.Vp8l;
using Xunit;

public class Vp8lDecoderTests
{
    private static string Fixtures => Path.Combine(System.AppContext.BaseDirectory, "fixtures", "vp8l");

    public static TheoryData<string> Files()
    {
        var d = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Fixtures, "*.webp").OrderBy(x => x))
            d.Add(Path.GetFileNameWithoutExtension(f));
        return d;
    }

    // dwebp is libwebp's own decoder. A lossless format decodes to exactly one answer,
    // so anything short of byte equality is a bug, not a tolerance.
    [Theory]
    [MemberData(nameof(Files))]
    public void MatchesDwebpByteForByte(string name)
    {
        var rgba = Vp8lDecoder.Decode(File.ReadAllBytes(Path.Combine(Fixtures, name + ".webp")), out int w, out int h);
        // "<byte count> <sha256>" of dwebp's pixels. A hash match is a byte match.
        var expected = File.ReadAllText(Path.Combine(Fixtures, name + ".sha256")).Trim().Split(' ');
        Assert.Equal(long.Parse(expected[0]), (long)w * h * 4);
        Assert.Equal(long.Parse(expected[0]), rgba.LongLength);
        using var sha = System.Security.Cryptography.SHA256.Create();
        Assert.Equal(expected[1], System.Convert.ToHexString(sha.ComputeHash(rgba)).ToLowerInvariant());
    }

    [Fact]
    public void RejectsLossy()
    {
        var lossy = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0,
                                 (byte)'W', (byte)'E', (byte)'B', (byte)'P', (byte)'V', (byte)'P', (byte)'8', (byte)' ' };
        var e = Assert.Throws<Vp8lException>(() => Vp8lDecoder.Decode(lossy, out _, out _));
        Assert.Contains("VP8 ", e.Message);
    }

    // The size comes from the header, so a tiny file can ask for 16384x16384; the cap is
    // checked before that allocation, and an image exactly at the cap still decodes.
    [Fact]
    public void RefusesAnImageLargerThanTheCap()
    {
        var webp = File.ReadAllBytes(Directory.GetFiles(Fixtures, "*.webp").OrderBy(x => x).First());
        Vp8lDecoder.Decode(webp, out int w, out int h);
        Vp8lDecoder.Decode(webp, out _, out _, (long)w * h);
        var e = Assert.Throws<Vp8lException>(() => Vp8lDecoder.Decode(webp, out _, out _, (long)w * h - 1));
        Assert.Contains("exceeds", e.Message);
    }
}
