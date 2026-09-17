using System;

namespace VDGS.Lod
{
    public struct LodBox
    {
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    }

    /// <summary>
    /// Picks one LOD level per leaf from how large it looks, a splat budget, and hysteresis.
    /// Allocation-free after construction.
    ///
    /// How large it LOOKS, not how far away it is. Distance alone spends the budget on
    /// whatever is nearest whatever its size: flying past Himeji castle at 65 m, the trees
    /// under the camera drew at the finest level while the keep - fifty metres across and
    /// filling half the frame - drew at the coarsest, and a single-level .ply beat it.
    /// A leaf's extent over its distance is what a pixel budget actually cares about.
    /// </summary>
    public sealed class LodSelector
    {
        /// <summary>
        /// Apparent size (leaf extent / distance) at which a leaf earns its finest level.
        /// Each halving of apparent size drops one level. Unitless, so it holds whatever
        /// scale the capture is placed at.
        /// </summary>
        public float LodDetail = 1f;
        public long Budget = 3_000_000;
        public const float Hysteresis = 0.1f;

        /// <summary>
        /// Spread of the per-leaf band offsets, as a fraction of the band.
        ///
        /// Leaves at the same distance share the same band edges, so the ground ahead of a
        /// moving camera changes level as one sheet - measured at up to 19.6% of the frame
        /// switching on a single tick. A fixed per-leaf offset breaks that into many small
        /// switches spread over distance: the same total work, far less of it at once.
        /// 0 disables it.
        /// </summary>
        public float BandJitter = 0f;

        private readonly LodBox[] m_Leaves;
        private readonly int m_LevelCount;
        private readonly int[] m_RunLeaf;
        private readonly int[] m_RunLevel;
        private readonly int m_RunCountN;

        // leaf * levelCount + level
        private readonly bool[] m_HasLevel;
        private readonly int[] m_CountAt;
        private readonly int[] m_MaxLevel;  // max(S); -1 if empty
        private readonly int[] m_NextLevel; // next coarser in S from cur; -1 if none

        // Longest side of each leaf box, in object units - the "how big is it" half of the
        // apparent-size test, fixed for the capture's life.
        private readonly float[] m_Extent;
        // Per-leaf band scale, 1 +/- BandJitter, derived from the leaf index alone so it is
        // stable across frames and runs - a jitter that moved would be its own flicker.
        private readonly float[] m_Jitter;
        private readonly float[] m_Dist;
        private readonly int[] m_Order;
        private readonly int[] m_Remembered; // step-3 result (hysteresis memory)
        private readonly int[] m_Display;    // after budget coarsening
        private readonly long[] m_ActivePerLevel;
        private readonly FarFirstComparer m_FarFirst;

        private bool m_HasHistory;
        private long m_ActiveSplats;

        /// <param name="runLeaf">Parallel with runLevel/runCount — one entry per run.</param>
        public LodSelector(LodBox[] leaves, int levelCount, int[] runLeaf, int[] runLevel, int[] runCount)
        {
            m_Leaves = leaves ?? throw new ArgumentNullException(nameof(leaves));
            m_LevelCount = levelCount;
            m_RunLeaf = runLeaf ?? throw new ArgumentNullException(nameof(runLeaf));
            m_RunLevel = runLevel ?? throw new ArgumentNullException(nameof(runLevel));
            if (runCount == null) throw new ArgumentNullException(nameof(runCount));
            if (levelCount < 1)
                throw new ArgumentOutOfRangeException(nameof(levelCount));
            if (runLeaf.Length != runLevel.Length || runLeaf.Length != runCount.Length)
                throw new ArgumentException("runLeaf/runLevel/runCount length mismatch");
            m_RunCountN = runLeaf.Length;

            int n = leaves.Length;
            int cells = n * levelCount;
            m_HasLevel = new bool[cells];
            m_CountAt = new int[cells];
            m_MaxLevel = new int[n];
            m_NextLevel = new int[cells];

            for (int i = 0; i < n; i++)
                m_MaxLevel[i] = -1;

            for (int r = 0; r < m_RunCountN; r++)
            {
                int leaf = runLeaf[r];
                int lv = runLevel[r];
                if ((uint)leaf >= (uint)n || (uint)lv >= (uint)levelCount)
                    throw new ArgumentOutOfRangeException(nameof(runLeaf), "run leaf/level out of range");
                int idx = leaf * levelCount + lv;
                m_HasLevel[idx] = true;
                m_CountAt[idx] = runCount[r];
            }

            for (int leaf = 0; leaf < n; leaf++)
            {
                int baseI = leaf * levelCount;
                int max = -1;
                for (int l = 0; l < levelCount; l++)
                    if (m_HasLevel[baseI + l]) max = l;
                m_MaxLevel[leaf] = max;
                for (int cur = 0; cur < levelCount; cur++)
                {
                    int next = -1;
                    for (int l = cur + 1; l < levelCount; l++)
                    {
                        if (m_HasLevel[baseI + l]) { next = l; break; }
                    }
                    m_NextLevel[baseI + cur] = next;
                }
            }

            m_Extent = new float[n];
            for (int i = 0; i < n; i++)
            {
                var b = leaves[i];
                float ex = b.MaxX - b.MinX, ey = b.MaxY - b.MinY, ez = b.MaxZ - b.MinZ;
                m_Extent[i] = Math.Max(ex, Math.Max(ey, ez));
            }
            m_Jitter = new float[n];
            for (int i = 0; i < n; i++)
            {
                // Wang integer hash, so neighbouring leaves land far apart in [-0.5, 0.5).
                uint h = (uint)i;
                h = (h ^ 61u) ^ (h >> 16); h *= 9u; h ^= h >> 4; h *= 0x27d4eb2du; h ^= h >> 15;
                m_Jitter[i] = h / 4294967296f - 0.5f;
            }
            m_Dist = new float[n];
            m_Order = new int[n];
            m_Remembered = new int[n];
            m_Display = new int[n];
            m_ActivePerLevel = new long[levelCount];
            m_FarFirst = new FarFirstComparer(m_Dist);
            for (int i = 0; i < n; i++)
            {
                m_Remembered[i] = -1;
                m_Display[i] = -1;
            }
        }

        public long ActiveSplats => m_ActiveSplats;
        public long[] ActivePerLevel => m_ActivePerLevel;

        public int LevelOfLeaf(int leaf) => m_Display[leaf];

        /// <summary>
        /// cam is in object space; worldScale converts object units to metres.
        /// Writes 1/0 per run into runActive. Returns true when any entry changed
        /// since the previous call (always true on the first call).
        /// </summary>
        public bool Update(float camX, float camY, float camZ, float worldScale, byte[] runActive)
        {
            if (runActive == null || runActive.Length < m_RunCountN)
                throw new ArgumentException("runActive length must be at least the run count", nameof(runActive));

            bool first = !m_HasHistory;
            int n = m_Leaves.Length;
            int L = m_LevelCount;
            float detail = LodDetail;
            if (detail <= 0f) detail = 1e-6f;

            // 1. Distance to closest point on each leaf AABB, in metres.
            for (int i = 0; i < n; i++)
            {
                var b = m_Leaves[i];
                float cx = camX < b.MinX ? b.MinX : (camX > b.MaxX ? b.MaxX : camX);
                float cy = camY < b.MinY ? b.MinY : (camY > b.MaxY ? b.MaxY : camY);
                float cz = camZ < b.MinZ ? b.MinZ : (camZ > b.MaxZ ? b.MaxZ : camZ);
                float dx = camX - cx, dy = camY - cy, dz = camZ - cz;
                m_Dist[i] = worldScale * (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                m_Order[i] = i;
            }

            // 2–3. Apparent-size band (+ hysteresis) → want → snap onto available set S.
            // Remembered for next frame is the step-3 result (not budget-coarsened).
            float loMul = 1f - Hysteresis;
            float hiMul = 1f + Hysteresis;
            for (int i = 0; i < n; i++)
            {
                // Apparent size: the leaf's longest side over its distance, both in metres.
                float size = m_Extent[i] * worldScale / Math.Max(m_Dist[i], 1e-4f);
                // The jitter offsets each leaf's thresholds so neighbours at one distance do
                // not all change level on the same tick.
                float detailI = detail * (1f + BandJitter * 2f * m_Jitter[i]);
                int want;
                int prev = m_Remembered[i];
                if (m_HasHistory && prev >= 0)
                {
                    // Band prev covers apparent sizes (lo, hi]; sizes shrink as levels rise.
                    float hi = prev == 0 ? float.PositiveInfinity : detailI / (float)(1 << (prev - 1));
                    float lo = prev == L - 1 ? 0f : detailI / (float)(1 << prev);
                    if (size <= hi * hiMul && size > lo * loMul)
                        want = prev;
                    else
                        want = SizeWant(size, detailI, L);
                }
                else
                {
                    want = SizeWant(size, detailI, L);
                }

                int maxS = m_MaxLevel[i];
                int chosen;
                if (maxS < 0 || want > maxS)
                    chosen = -1;
                else
                    chosen = FirstLevelAtLeast(i, want);
                m_Remembered[i] = chosen;
                m_Display[i] = chosen;
            }

            // 4. While over budget, coarsen farthest leaves one step at a time.
            long total = SumDisplayCounts();
            if (total > Budget)
            {
                Array.Sort(m_Order, 0, n, m_FarFirst);
                while (total > Budget)
                {
                    bool advanced = false;
                    for (int oi = 0; oi < n && total > Budget; oi++)
                    {
                        int leaf = m_Order[oi];
                        int cur = m_Display[leaf];
                        int maxS = m_MaxLevel[leaf];
                        if (cur < 0 || cur == maxS)
                            continue;
                        int next = m_NextLevel[leaf * L + cur];
                        if (next < 0)
                            continue;
                        total -= m_CountAt[leaf * L + cur];
                        total += m_CountAt[leaf * L + next];
                        m_Display[leaf] = next;
                        advanced = true;
                    }
                    if (!advanced)
                        break;
                }
            }

            // Tally + write run active flags; detect change vs previous runActive contents.
            for (int l = 0; l < L; l++)
                m_ActivePerLevel[l] = 0;
            m_ActiveSplats = 0;
            for (int i = 0; i < n; i++)
            {
                int lv = m_Display[i];
                if (lv < 0) continue;
                int c = m_CountAt[i * L + lv];
                m_ActivePerLevel[lv] += c;
                m_ActiveSplats += c;
            }

            bool changed = first;
            for (int r = 0; r < m_RunCountN; r++)
            {
                byte v = (byte)(m_Display[m_RunLeaf[r]] == m_RunLevel[r] ? 1 : 0);
                if (runActive[r] != v)
                    changed = true;
                runActive[r] = v;
            }

            m_HasHistory = true;
            return changed;
        }

        private int FirstLevelAtLeast(int leaf, int want)
        {
            int baseI = leaf * m_LevelCount;
            for (int l = want; l < m_LevelCount; l++)
            {
                if (m_HasLevel[baseI + l])
                    return l;
            }
            return -1;
        }

        private long SumDisplayCounts()
        {
            long total = 0;
            int L = m_LevelCount;
            for (int i = 0; i < m_Leaves.Length; i++)
            {
                int lv = m_Display[i];
                if (lv >= 0)
                    total += m_CountAt[i * L + lv];
            }
            return total;
        }

        /// <summary>
        /// Level from apparent size: the finest while the leaf looks at least `detail`
        /// across, one level coarser for each halving after that.
        /// </summary>
        private static int SizeWant(float size, float detail, int L)
        {
            if (size >= detail) return 0;
            if (size <= 0f) return L - 1;
            int w = (int)Math.Floor(Math.Log(detail / (double)size) / Math.Log(2.0)) + 1;
            if (w < 0) w = 0;
            if (w > L - 1) w = L - 1;
            return w;
        }

        private sealed class FarFirstComparer : System.Collections.Generic.IComparer<int>
        {
            private readonly float[] m_Dist;
            public FarFirstComparer(float[] dist) { m_Dist = dist; }
            public int Compare(int a, int b) => m_Dist[b].CompareTo(m_Dist[a]);
        }
    }
}
