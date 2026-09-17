using System.IO;
using UnityEngine;
using UnityEditor;
using VDGS;

/// <summary>
/// Load the same capture as .ply (splat-transform's decode of the .sog) and as .sog, and
/// report the largest disagreement per attribute. Both go through the resident layout, so
/// this checks mirroring, packing and the palette in one pass.
///
///   Unity -batchmode -quit -nographics -projectPath unity/VDGSBundler \
///         -executeMethod SogCheck.Run -vdgsPly &lt;sh-decoded.ply&gt; -vdgsSog &lt;sh.sog&gt;
///
/// Streamed SOG table check:
///   ... -executeMethod SogCheck.Run -vdgsSsog &lt;ssog-dir&gt;
/// </summary>
public static class SogCheck
{
    public static void Run()
    {
        string ssog = Arg("-vdgsSsog");
        if (!string.IsNullOrEmpty(ssog))
        {
            CheckSsog(ssog);
            return;
        }

        var ply = PlyLoader.Load(Arg("-vdgsPly"), out var e1);
        var sog = SogLoader.Load(Arg("-vdgsSog"), out var e2);
        if (ply == null || sog == null) { Debug.LogError("SOGCHECK load failed: " + e1 + " " + e2); EditorApplicationExit(1); return; }
        if (ply.SplatCount != sog.SplatCount) { Debug.LogError("SOGCHECK count " + ply.SplatCount + " vs " + sog.SplatCount); EditorApplicationExit(1); return; }

        float pos = MaxAbsDiffFloat(ply.PosData, sog.PosData, 0, 4);
        // other.bin: ply stride 16 (rot + float3 scale), sog stride 18 (+ ushort sh index)
        float rot = 0, scale = 0, sh = 0;
        for (int i = 0; i < ply.SplatCount; i++)
        {
            rot = Mathf.Max(rot, System.BitConverter.ToUInt32(ply.OtherData, i * 16) == System.BitConverter.ToUInt32(sog.OtherData, i * 18) ? 0 : 1);
            for (int k = 0; k < 3; k++)
                scale = Mathf.Max(scale, Mathf.Abs(System.BitConverter.ToSingle(ply.OtherData, i * 16 + 4 + k * 4)
                                                 - System.BitConverter.ToSingle(sog.OtherData, i * 18 + 4 + k * 4)));
            // a bundled .sog has one chunk and no LOD table, so its palette starts at row 0
            int row = System.BitConverter.ToUInt16(sog.OtherData, i * 18 + 16);
            for (int b = 0; b < 90; b += 2)
                sh = Mathf.Max(sh, Mathf.Abs(Mathf.HalfToFloat(System.BitConverter.ToUInt16(ply.ShData, i * 96 + b))
                                           - Mathf.HalfToFloat(System.BitConverter.ToUInt16(sog.ShData, row * 96 + b))));
        }
        float color = MaxAbsDiffHalf(ply.ColorData, sog.ColorData);
        Debug.Log($"SOGCHECK splats {ply.SplatCount} pos {pos:G3} rotMismatch {rot} scale {scale:G3} color {color:G3} sh {sh:G3}");
        EditorApplicationExit(pos < 1e-4f && rot == 0 && scale < 1e-4f && color < 2e-3f && sh < 2e-3f ? 0 : 1);
    }

    private static void CheckSsog(string dir)
    {
        var sog = SogLoader.Load(dir, out var err);
        if (sog == null)
        {
            Debug.LogError("SOGCHECK ssog load failed: " + err);
            EditorApplicationExit(1);
            return;
        }
        if (sog.Lod == null)
        {
            Debug.LogError("SOGCHECK ssog missing LodInfo");
            EditorApplicationExit(1);
            return;
        }

        var lod = sog.Lod;
        long sumCounts = 0;
        // Re-read counts from lod-meta for the Σ check.
        string lodMetaPath = Path.Combine(dir, "lod-meta.json");
        var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(lodMetaPath));
        foreach (var c in (Newtonsoft.Json.Linq.JArray)root["counts"])
            sumCounts += (int)c;

        if (sog.SplatCount != sumCounts)
        {
            Debug.LogError($"SOGCHECK ssog SplatCount {sog.SplatCount} != sum counts {sumCounts}");
            EditorApplicationExit(1);
            return;
        }

        for (int r = 0; r < lod.RunOffset.Length; r++)
        {
            int lo = lod.RunOffset[r];
            int hi = lo + lod.RunCount[r];
            for (int i = lo; i < hi; i++)
            {
                if (lod.RunOfSplat[i] != (uint)r)
                {
                    Debug.LogError($"SOGCHECK ssog RunOfSplat[{i}]={lod.RunOfSplat[i]} expected {r}");
                    EditorApplicationExit(1);
                    return;
                }
            }
        }

        // RunShBase follows file order; global splat order is file concatenation, so
        // sorting runs by RunOffset must see non-decreasing RunShBase.
        var order = new int[lod.RunOffset.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        System.Array.Sort(order, (a, b) => lod.RunOffset[a].CompareTo(lod.RunOffset[b]));
        for (int i = 1; i < order.Length; i++)
        {
            int prev = lod.RunShBase[order[i - 1]];
            int cur = lod.RunShBase[order[i]];
            if (cur < prev)
            {
                Debug.LogError($"SOGCHECK ssog RunShBase not monotonic in file order: {prev} then {cur}");
                EditorApplicationExit(1);
                return;
            }
        }

        Debug.Log("SOGCHECK ssog ok");
        EditorApplicationExit(0);
    }

    private static float MaxAbsDiffFloat(byte[] a, byte[] b, int start, int stride)
    {
        float m = 0;
        for (int o = start; o + 4 <= a.Length; o += stride)
            m = Mathf.Max(m, Mathf.Abs(System.BitConverter.ToSingle(a, o) - System.BitConverter.ToSingle(b, o)));
        return m;
    }

    private static float MaxAbsDiffHalf(byte[] a, byte[] b)
    {
        float m = 0;
        for (int o = 0; o + 2 <= a.Length; o += 2)
            m = Mathf.Max(m, Mathf.Abs(Mathf.HalfToFloat(System.BitConverter.ToUInt16(a, o)) - Mathf.HalfToFloat(System.BitConverter.ToUInt16(b, o))));
        return m;
    }

    private static void EditorApplicationExit(int code) => EditorApplication.Exit(code);

    private static string Arg(string name)
    {
        var a = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return null;
    }
}
