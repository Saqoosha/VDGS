using System.Linq;
using VDGS.Lod;
using Xunit;

public class LodSelectorTests
{
    // A row of 1 m leaves along +x at x = 0, 10, 20, ... each with levels 0..2 of 100/50/25
    // splats. The camera sits inside leaf 0, so leaf i is about 10*i - 0.5 m away.
    private static LodSelector Row(int leaves, int levels = 3, int[] missingTop = null, float size = 1f)
    {
        var boxes = Enumerable.Range(0, leaves)
            .Select(i => new LodBox { MinX = i * 10, MaxX = i * 10 + size, MaxY = size, MaxZ = size }).ToArray();
        var leaf = new System.Collections.Generic.List<int>();
        var level = new System.Collections.Generic.List<int>();
        var count = new System.Collections.Generic.List<int>();
        for (int i = 0; i < leaves; i++)
            for (int l = 0; l < levels; l++)
            {
                if (missingTop != null && missingTop.Contains(i) && l == levels - 1) continue;
                leaf.Add(i); level.Add(l); count.Add(100 >> l);
            }
        return new LodSelector(boxes, levels, leaf.ToArray(), level.ToArray(), count.ToArray());
    }

    // Leaves stacked on the same 1 m box, so distance is identical and only the per-leaf
    // jitter can separate them.
    private static LodSelector Stack(int leaves, int levels = 3)
    {
        var boxes = Enumerable.Range(0, leaves)
            .Select(_ => new LodBox { MinX = 10f, MaxX = 11f, MaxY = 1f, MaxZ = 1f }).ToArray();
        var leaf = new System.Collections.Generic.List<int>();
        var level = new System.Collections.Generic.List<int>();
        var count = new System.Collections.Generic.List<int>();
        for (int i = 0; i < leaves; i++)
            for (int l = 0; l < levels; l++) { leaf.Add(i); level.Add(l); count.Add(100 >> l); }
        return new LodSelector(boxes, levels, leaf.ToArray(), level.ToArray(), count.ToArray());
    }

    // detail 0.2 means "finest while the leaf looks at least a fifth of its distance across".
    // A 1 m leaf then earns level 0 within 5 m, level 1 to 10 m, level 2 beyond.
    [Fact]
    public void LevelFollowsApparentSize()
    {
        var s = Row(4); s.LodDetail = 0.2f;
        s.Update(0.5f, 0.5f, 0.5f, 1f, new byte[12]);
        Assert.Equal(0, s.LevelOfLeaf(0));   // inside it
        Assert.Equal(1, s.LevelOfLeaf(1));   // 9.5 m away, looks 0.105 across
        Assert.Equal(2, s.LevelOfLeaf(2));   // 19.5 m
        Assert.Equal(2, s.LevelOfLeaf(3));   // 29.5 m, clamped at the coarsest level
    }

    // The whole point of the rule: a leaf ten times the size holds its detail ten times
    // further out. Distance alone drew Himeji's keep at the coarsest level from 65 m while
    // the grass under the camera got the finest.
    [Fact]
    public void BiggerLeavesKeepDetailFurtherAway()
    {
        var small = Row(2, size: 1f); small.LodDetail = 0.2f;
        var big = Row(2, size: 10f); big.LodDetail = 0.2f;
        small.Update(0.5f, 0.5f, 0.5f, 1f, new byte[6]);
        big.Update(0.5f, 0.5f, 0.5f, 1f, new byte[6]);
        Assert.Equal(1, small.LevelOfLeaf(1));
        Assert.Equal(0, big.LevelOfLeaf(1));
    }

    // Placing a capture at 44x moves every leaf and every distance by the same factor, so
    // the levels must not move with it - the old distance bands did, which is why the
    // default stopped working the moment a scene was scaled up.
    [Fact]
    public void ScaleCancelsOut()
    {
        var a = Row(4); a.LodDetail = 0.2f;
        var b = Row(4); b.LodDetail = 0.2f;
        a.Update(0.5f, 0.5f, 0.5f, 1f, new byte[12]);
        b.Update(0.5f, 0.5f, 0.5f, 44f, new byte[12]);
        for (int leaf = 0; leaf < 4; leaf++)
            Assert.Equal(a.LevelOfLeaf(leaf), b.LevelOfLeaf(leaf));
    }

    [Fact]
    public void ExactlyOneRunPerDrawnLeafIsActive()
    {
        var s = Row(4); s.LodDetail = 0.2f;
        var active = new byte[12];
        s.Update(0.5f, 0.5f, 0.5f, 1f, active);
        for (int leaf = 0; leaf < 4; leaf++)
            Assert.Equal(1, Enumerable.Range(0, 3).Sum(l => active[leaf * 3 + l]));
        Assert.Equal(100 + 50 + 25 + 25, s.ActiveSplats);
        Assert.Equal(new long[] { 100, 50, 50 }, s.ActivePerLevel);
    }

    [Fact]
    public void BudgetCoarsensTheFarthestFirst()
    {
        var s = Row(4); s.LodDetail = 0.001f;           // tiny threshold: every leaf wants level 0, 400 splats
        s.Budget = 350;
        s.Update(0.5f, 0.5f, 0.5f, 1f, new byte[12]);
        Assert.Equal(0, s.LevelOfLeaf(0));
        Assert.Equal(0, s.LevelOfLeaf(1));
        Assert.Equal(0, s.LevelOfLeaf(2));
        Assert.Equal(1, s.LevelOfLeaf(3));
        Assert.Equal(350, s.ActiveSplats);
    }

    [Fact]
    public void BudgetNeverDropsData()
    {
        var s = Row(2); s.LodDetail = 0.001f; s.Budget = 1;
        s.Update(0.5f, 0.5f, 0.5f, 1f, new byte[6]);
        Assert.Equal(2, s.LevelOfLeaf(0));
        Assert.Equal(2, s.LevelOfLeaf(1));
        Assert.Equal(50, s.ActiveSplats);
    }

    [Fact]
    public void LeafWithoutTheWantedCoarseLevelIsNotDrawn()
    {
        var s = Row(3, 3, missingTop: new[] { 2 }); s.LodDetail = 0.2f;
        var active = new byte[8];
        s.Update(0.5f, 0.5f, 0.5f, 1f, active);
        Assert.Equal(-1, s.LevelOfLeaf(2));
        Assert.Equal(0, active[6] + active[7]);
    }

    // Level 1 covers apparent sizes (0.1, 0.2]; the margins stretch that to (0.09, 0.22].
    [Fact]
    public void HysteresisHoldsInsideTheMargin()
    {
        var s = Row(2); s.LodDetail = 0.2f;             // leaf 1 spans x = 10..11
        var a = new byte[6];
        s.Update(0.5f, 0.5f, 0.5f, 1f, a);              // 9.5 m -> 0.105 -> level 1
        Assert.Equal(1, s.LevelOfLeaf(1));
        s.Update(-0.6f, 0.5f, 0.5f, 1f, a);             // 10.6 m -> 0.094, still inside 0.09
        Assert.Equal(1, s.LevelOfLeaf(1));
        s.Update(-1.6f, 0.5f, 0.5f, 1f, a);             // 11.6 m -> 0.086 -> drops
        Assert.Equal(2, s.LevelOfLeaf(1));
        s.Update(0.5f, 0.5f, 0.5f, 1f, a);              // back to 0.105, inside level 2's margin
        Assert.Equal(2, s.LevelOfLeaf(1));
        s.Update(5.5f, 0.5f, 0.5f, 1f, a);              // 4.5 m -> 0.22 -> back up
        Assert.True(s.LevelOfLeaf(1) < 2);
    }

    [Fact]
    public void ReportsChangeOnlyWhenSomethingChanged()
    {
        var s = Row(2); s.LodDetail = 0.2f;
        var a = new byte[6];
        Assert.True(s.Update(0.5f, 0.5f, 0.5f, 1f, a));
        Assert.False(s.Update(0.5f, 0.5f, 0.5f, 1f, a));
    }

    // The jitter must not move between calls: a threshold that wandered would switch levels
    // on its own, which is the thing it exists to prevent.
    [Fact]
    public void JitterIsStableAcrossCalls()
    {
        var s = Row(6); s.LodDetail = 0.2f; s.BandJitter = 0.4f;
        var a = new byte[18];
        s.Update(0.5f, 0.5f, 0.5f, 1f, a);
        var first = Enumerable.Range(0, 6).Select(s.LevelOfLeaf).ToArray();
        Assert.False(s.Update(0.5f, 0.5f, 0.5f, 1f, a));
        Assert.Equal(first, Enumerable.Range(0, 6).Select(s.LevelOfLeaf).ToArray());
    }

    // What the jitter is for: without it, leaves that look the same size cross a band edge
    // on the same tick and a whole sheet of ground switches at once. With it, they do not.
    // Stability alone cannot show this — it holds at jitter 0 too.
    [Fact]
    public void JitterSplitsLeavesThatWouldSwitchTogether()
    {
        Assert.False(SplitsSomewhere(0f));
        Assert.True(SplitsSomewhere(0.4f));
    }

    // True when some camera distance makes the stacked leaves disagree about their level.
    private static bool SplitsSomewhere(float jitter)
    {
        for (int step = 0; step <= 200; step++)
        {
            var s = Stack(8); s.LodDetail = 0.2f; s.BandJitter = jitter;
            s.Update(0.5f - step * 0.25f, 0.5f, 0.5f, 1f, new byte[24]);
            var levels = Enumerable.Range(0, 8).Select(s.LevelOfLeaf).ToArray();
            if (levels.Distinct().Count() > 1) return true;
        }
        return false;
    }
}
