using VDGS;
using Xunit;

public class SplatOrientationTests
{
    // Unity applies euler angles Z, then X, then Y, so the Y slot is the last rotation
    // and is therefore a world-space yaw. That is exactly what Turn is, which is why it
    // can be dropped into the Y component whatever Up chose.
    [Theory]
    [InlineData("+y", 0f, 0f, 0f)]
    [InlineData("-y", 180f, 0f, 0f)]
    [InlineData("+z", -90f, 0f, 0f)]
    [InlineData("-z", 90f, 0f, 0f)]
    [InlineData("+x", 0f, 0f, 90f)]
    [InlineData("-x", 0f, 0f, -90f)]
    public void ComposesTheUpAxis(string up, float ex, float ey, float ez)
    {
        SplatOrientation.Compose(up, 0f, out var x, out var y, out var z);
        Assert.Equal(ex, x, 3);
        Assert.Equal(ey, y, 3);
        Assert.Equal(ez, z, 3);
    }

    [Fact]
    public void TurnGoesIntoTheYawSlotWithoutDisturbingUp()
    {
        SplatOrientation.Compose("+z", 37.5f, out var x, out var y, out var z);
        Assert.Equal(-90f, x, 3);
        Assert.Equal(37.5f, y, 3);
        Assert.Equal(0f, z, 3);
    }

    [Fact]
    public void TurnWrapsIntoZeroToThreeSixty()
    {
        SplatOrientation.Compose("+y", 400f, out _, out var y, out _);
        Assert.Equal(40f, y, 3);
        SplatOrientation.Compose("+y", -10f, out _, out var y2, out _);
        Assert.Equal(350f, y2, 3);
    }

    // An unreadable value must not silently become a different orientation - identity is
    // the one answer that leaves the capture as authored.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sideways")]
    public void AnUnknownUpFallsBackToIdentity(string up)
    {
        SplatOrientation.Compose(up, 0f, out var x, out var y, out var z);
        Assert.Equal(0f, x, 3);
        Assert.Equal(0f, y, 3);
        Assert.Equal(0f, z, 3);
    }

    // The network door and the file-load door both need to tell a real axis from
    // garbage before it reaches Compose, whose own default arm exists precisely to
    // stay quiet about garbage rather than reject it.
    [Theory]
    [InlineData("+x")]
    [InlineData("-x")]
    [InlineData("+y")]
    [InlineData("-y")]
    [InlineData("+z")]
    [InlineData("-z")]
    public void IsUpAcceptsTheSixAxes(string up)
    {
        Assert.True(SplatOrientation.IsUp(up));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sideways")]
    public void IsUpRejectsAnythingElse(string up)
    {
        Assert.False(SplatOrientation.IsUp(up));
    }
}
