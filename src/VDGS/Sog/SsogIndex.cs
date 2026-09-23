using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace VDGS.Sog
{
    public struct SsogLeaf
    {
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    }

    public struct SsogRun
    {
        public int File, Offset, Count, Leaf, Level;
    }

    public sealed class SsogIndex
    {
        /// <summary>LodSelector shifts by the level; SuperSplat writes 5.</summary>
        public const int MaxLevels = 16;

        public int Count;
        public int[] Counts;
        public int LevelCount;
        public string[] Files;          // relative meta.json paths
        public string Environment;      // relative meta.json path or null
        public SsogLeaf[] Leaves;
        public SsogRun[] Runs;          // leaf order, then level ascending

        public static SsogIndex Parse(byte[] lodMetaJson)
        {
            if (lodMetaJson == null || lodMetaJson.Length == 0)
                throw new SogException("empty lod-meta.json");

            JObject root;
            try
            {
                root = JObject.Parse(Encoding.UTF8.GetString(lodMetaJson));
            }
            catch (Exception e)
            {
                throw new SogException("lod-meta.json parse failed: " + e.Message);
            }

            var idx = new SsogIndex();
            idx.Count = ReqInt(root, "count", "lod-meta.json");
            idx.LevelCount = ReqInt(root, "lodLevels", "lod-meta.json");
            if (idx.Count < 0) throw new SogException("lod-meta.json count negative");
            if (idx.LevelCount < 1 || idx.LevelCount > MaxLevels)
                throw new SogException("lod-meta.json lodLevels " + idx.LevelCount + " outside 1.." + MaxLevels);
            var countsTok = root["counts"] as JArray;
            if (countsTok == null) throw new SogException("lod-meta.json missing counts");
            idx.Counts = countsTok.ToObject<int[]>();
            if (idx.Counts == null || idx.Counts.Length != idx.LevelCount)
                throw new SogException("lod-meta.json counts length != lodLevels");
            foreach (int c in idx.Counts)
                if (c < 0) throw new SogException("lod-meta.json counts has a negative entry");

            var filesTok = root["filenames"] as JArray;
            if (filesTok == null) throw new SogException("lod-meta.json missing filenames");
            idx.Files = filesTok.ToObject<string[]>();
            if (idx.Files == null) throw new SogException("lod-meta.json filenames null");
            foreach (string f in idx.Files)
                if (string.IsNullOrEmpty(f)) throw new SogException("lod-meta.json filenames has an empty entry");

            JToken env = root["environment"];
            idx.Environment = env == null || env.Type == JTokenType.Null ? null : env.Value<string>();

            JToken tree = root["tree"];
            if (tree == null) throw new SogException("lod-meta.json missing tree");

            var leaves = new List<SsogLeaf>();
            var runs = new List<SsogRun>();
            Walk(tree, idx, leaves, runs);
            idx.Leaves = leaves.ToArray();
            idx.Runs = runs.ToArray();
            return idx;
        }

        private static void Walk(JToken node, SsogIndex idx, List<SsogLeaf> leaves, List<SsogRun> runs)
        {
            JToken children = node["children"];
            if (children != null && children.Type == JTokenType.Array)
            {
                var arr = (JArray)children;
                if (arr.Count != 2)
                    throw new SogException("tree node children must be a pair");
                Walk(arr[0], idx, leaves, runs);
                Walk(arr[1], idx, leaves, runs);
                return;
            }

            var lods = node["lods"] as JObject;
            if (lods == null)
                throw new SogException("tree leaf missing lods");

            JToken bound = node["bound"];
            if (bound == null) throw new SogException("tree leaf missing bound");
            float[] min = BoundVec(bound["min"], "min");
            float[] max = BoundVec(bound["max"], "max");
            for (int a = 0; a < 3; a++)
                if (!(min[a] <= max[a])) throw new SogException("tree leaf bound min > max");

            int leafIndex = leaves.Count;
            leaves.Add(new SsogLeaf
            {
                MinX = min[0], MinY = min[1], MinZ = min[2],
                MaxX = max[0], MaxY = max[1], MaxZ = max[2]
            });

            // Emit levels in ascending order so Runs are leaf-major, level-ascending.
            var leafRuns = new List<SsogRun>();
            foreach (JProperty p in lods.Properties())
            {
                if (!int.TryParse(p.Name, out int lv))
                    throw new SogException("lod key not an int: " + p.Name);
                if (lv < 0 || lv >= idx.LevelCount)
                    throw new SogException("lod level " + lv + " outside 0.." + (idx.LevelCount - 1));
                foreach (var seen in leafRuns)
                    if (seen.Level == lv) throw new SogException("lod level " + lv + " appears twice in one leaf");
                var r = p.Value as JObject;
                if (r == null) throw new SogException("lod " + p.Name + " is not an object");
                var run = new SsogRun
                {
                    File = ReqInt(r, "file", "lod"),
                    Offset = ReqInt(r, "offset", "lod"),
                    Count = ReqInt(r, "count", "lod"),
                    Leaf = leafIndex,
                    Level = lv
                };
                if (run.File < 0 || run.File >= idx.Files.Length)
                    throw new SogException("lod file " + run.File + " outside 0.." + (idx.Files.Length - 1));
                if (run.Offset < 0 || run.Count < 0)
                    throw new SogException("lod offset/count negative");
                leafRuns.Add(run);
            }
            leafRuns.Sort((x, y) => x.Level.CompareTo(y.Level));
            runs.AddRange(leafRuns);
        }

        private static int ReqInt(JObject obj, string key, string where)
        {
            JToken t = obj[key];
            if (t == null || t.Type != JTokenType.Integer)
                throw new SogException(where + " needs an integer " + key);
            long v = t.ToObject<long>();
            if (v < int.MinValue || v > int.MaxValue)
                throw new SogException(where + " " + key + " out of range");
            return (int)v;
        }

        private static float[] BoundVec(JToken tok, string name)
        {
            var arr = tok as JArray;
            if (arr == null || arr.Count != 3)
                throw new SogException("bound." + name + " must be a length-3 array");
            return new[] { arr[0].Value<float>(), arr[1].Value<float>(), arr[2].Value<float>() };
        }
    }
}
