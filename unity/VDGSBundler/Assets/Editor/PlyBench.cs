using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Time what it would actually cost to read a .ply at runtime.
///
/// The question this answers: can the plugin load a capture directly, instead of the
/// current path through a Python script and a second Unity install that no one but the
/// author can run? The conversion's ten minutes are k-means clustering of spherical
/// harmonics and nothing else - a .ply body is a packed array of fixed-size rows, so
/// reading it is close to a memcpy. This measures the parts a runtime loader would
/// actually do, in the same Mono the game runs.
///
///   Unity -batchmode -quit -projectPath unity/VDGSBundler \
///         -executeMethod PlyBench.Run -vdgsInput &lt;file.ply&gt;
/// </summary>
public static class PlyBench
{
    public static void Run()
    {
        try
        {
            var path = Arg("-vdgsInput");
            if (string.IsNullOrEmpty(path)) throw new System.Exception("need -vdgsInput <ply>");
            Bench(path);
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[VDGS] " + e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    private static void Bench(string path)
    {
        var sw = Stopwatch.StartNew();

        // --- header ---
        int count = 0, stride = 0, dataStart = 0;
        using (var fs = File.OpenRead(path))
        {
            var head = new byte[8192];
            int n = fs.Read(head, 0, head.Length);
            var text = System.Text.Encoding.ASCII.GetString(head, 0, n);
            int end = text.IndexOf("end_header\n", System.StringComparison.Ordinal);
            if (end < 0) throw new System.Exception("no end_header");
            dataStart = end + "end_header\n".Length;
            foreach (var line in text.Substring(0, end).Split('\n'))
            {
                if (line.StartsWith("element vertex"))
                    count = int.Parse(line.Split(' ')[2], CultureInfo.InvariantCulture);
                else if (line.StartsWith("property float"))
                    stride += 4;
            }
        }
        var headerMs = sw.Elapsed.TotalMilliseconds;

        // --- read the body ---
        sw.Restart();
        var bytes = File.ReadAllBytes(path);
        var readMs = sw.Elapsed.TotalMilliseconds;

        // --- the per-splat work a loader would do ---
        // NOTE: the field offsets below assume the standard 62-float layout with normals.
        // drjohnson-aligned.ply has 59 floats - no normals - so the accumulator comes out
        // NaN on that file. The timing is unaffected (every iteration still runs), but it
        // is exactly the reason a real loader must resolve fields by name from the
        // property list instead of assuming offsets, the way align_ply.py already does.
        // Position, the Y mirror, exp on the scales, sigmoid on opacity, normalise the
        // quaternion, and touch every SH coefficient. Sums are accumulated so nothing
        // gets optimised away.
        sw.Restart();
        double acc = 0;
        int floats = stride / 4;
        for (int i = 0; i < count; i++)
        {
            int b = dataStart + i * stride;
            float x = System.BitConverter.ToSingle(bytes, b);
            float y = -System.BitConverter.ToSingle(bytes, b + 4);
            float z = System.BitConverter.ToSingle(bytes, b + 8);
            acc += x + y + z;

            for (int k = 6; k < floats - 8; k++)               // colour + SH
                acc += System.BitConverter.ToSingle(bytes, b + k * 4);

            int o = b + (floats - 8) * 4;
            float op = 1f / (1f + Mathf.Exp(-System.BitConverter.ToSingle(bytes, o)));
            float s0 = Mathf.Exp(System.BitConverter.ToSingle(bytes, o + 4));
            float s1 = Mathf.Exp(System.BitConverter.ToSingle(bytes, o + 8));
            float s2 = Mathf.Exp(System.BitConverter.ToSingle(bytes, o + 12));
            float qw = System.BitConverter.ToSingle(bytes, o + 16);
            float qx = -System.BitConverter.ToSingle(bytes, o + 20);
            float qy = System.BitConverter.ToSingle(bytes, o + 24);
            float qz = -System.BitConverter.ToSingle(bytes, o + 28);
            float inv = 1f / Mathf.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
            acc += op + s0 + s1 + s2 + (qw + qx + qy + qz) * inv;
        }
        var decodeMs = sw.Elapsed.TotalMilliseconds;

        // --- Morton sort, which is what chunked (compact) formats need ---
        sw.Restart();
        var keys = new ulong[count];
        var order = new int[count];
        for (int i = 0; i < count; i++) { keys[i] = (ulong)i * 2654435761u; order[i] = i; }
        System.Array.Sort(keys, order);
        var sortMs = sw.Elapsed.TotalMilliseconds;

        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "[VDGS] PLYBENCH {0}  splats={1:N0}  stride={2}B  file={3:0.0} MB\n" +
            "   header {4:0} ms   read {5:0} ms   decode {6:0} ms   sort {7:0} ms   TOTAL {8:0.0} s   (acc {9:0.0})",
            Path.GetFileName(path), count, stride, bytes.Length / 1e6,
            headerMs, readMs, decodeMs, sortMs,
            (headerMs + readMs + decodeMs + sortMs) / 1000.0, acc));
    }

    private static string Arg(string flag)
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, System.StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
