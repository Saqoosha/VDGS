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
            idx.Count = root.Value<int>("count");
            idx.LevelCount = root.Value<int>("lodLevels");
            var countsTok = root["counts"] as JArray;
            if (countsTok == null) throw new SogException("lod-meta.json missing counts");
            idx.Counts = countsTok.ToObject<int[]>();
            if (idx.Counts == null || idx.Counts.Length != idx.LevelCount)
                throw new SogException("lod-meta.json counts length != lodLevels");

            var filesTok = root["filenames"] as JArray;
            if (filesTok == null) throw new SogException("lod-meta.json missing filenames");
            idx.Files = filesTok.ToObject<string[]>();
            if (idx.Files == null) throw new SogException("lod-meta.json filenames null");

            JToken env = root["environment"];
            idx.Environment = env == null || env.Type == JTokenType.Null ? null : env.Value<string>();

            JToken tree = root["tree"];
            if (tree == null) throw new SogException("lod-meta.json missing tree");

            var leaves = new List<SsogLeaf>();
            var runs = new List<SsogRun>();
            Walk(tree, leaves, runs);
            idx.Leaves = leaves.ToArray();
            idx.Runs = runs.ToArray();
            return idx;
        }

        private static void Walk(JToken node, List<SsogLeaf> leaves, List<SsogRun> runs)
        {
            JToken children = node["children"];
            if (children != null && children.Type == JTokenType.Array)
            {
                var arr = (JArray)children;
                if (arr.Count != 2)
                    throw new SogException("tree node children must be a pair");
                Walk(arr[0], leaves, runs);
                Walk(arr[1], leaves, runs);
                return;
            }

            JToken lods = node["lods"];
            if (lods == null)
                throw new SogException("tree leaf missing lods");

            JToken bound = node["bound"];
            if (bound == null) throw new SogException("tree leaf missing bound");
            float[] min = BoundVec(bound["min"], "min");
            float[] max = BoundVec(bound["max"], "max");

            int leafIndex = leaves.Count;
            leaves.Add(new SsogLeaf
            {
                MinX = min[0], MinY = min[1], MinZ = min[2],
                MaxX = max[0], MaxY = max[1], MaxZ = max[2]
            });

            // Emit levels in ascending order so Runs are leaf-major, level-ascending.
            var levelKeys = new List<int>();
            foreach (JProperty p in ((JObject)lods).Properties())
            {
                if (!int.TryParse(p.Name, out int lv))
                    throw new SogException("lod key not an int: " + p.Name);
                levelKeys.Add(lv);
            }
            levelKeys.Sort();
            foreach (int lv in levelKeys)
            {
                JToken r = lods[lv.ToString()];
                runs.Add(new SsogRun
                {
                    File = r.Value<int>("file"),
                    Offset = r.Value<int>("offset"),
                    Count = r.Value<int>("count"),
                    Leaf = leafIndex,
                    Level = lv
                });
            }
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
