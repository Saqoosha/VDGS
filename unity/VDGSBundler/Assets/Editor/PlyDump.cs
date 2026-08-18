using System.IO;
using UnityEditor;
using UnityEngine;
using VDGS;

/// <summary>
/// Run PlyLoader and write the buffers it produced, so they can be diffed byte for byte
/// against the offline converter's output for the same file.
///
/// Comparing renders only says "different"; comparing buffers says which one.
///
///   Unity -batchmode -quit -nographics -projectPath unity/VDGSBundler \
///         -executeMethod PlyDump.Run -vdgsInput &lt;file.ply&gt; -vdgsOut &lt;dir&gt; [-vdgsPlyNoMirror 1]
/// </summary>
public static class PlyDump
{
    public static void Run()
    {
        try
        {
            var input = Arg("-vdgsInput");
            var outDir = Arg("-vdgsOut");
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(outDir))
                throw new System.Exception("usage: -vdgsInput <ply> -vdgsOut <dir>");

            var data = PlyLoader.Load(input, out var error, Arg("-vdgsPlyNoMirror") == null);
            if (data == null) throw new System.Exception(error);

            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, "pos.bin"), data.PosData);
            File.WriteAllBytes(Path.Combine(outDir, "other.bin"), data.OtherData);
            File.WriteAllBytes(Path.Combine(outDir, "color.bin"), data.ColorData);
            File.WriteAllBytes(Path.Combine(outDir, "sh.bin"), data.ShData);
            Debug.Log($"[VDGS] PLYDUMP {data.SplatCount:N0} splats -> {outDir}  " +
                      $"pos={data.PosData.Length} other={data.OtherData.Length} " +
                      $"color={data.ColorData.Length} sh={data.ShData.Length}\n" +
                      $"   bounds {data.BoundsMin} .. {data.BoundsMax}");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[VDGS] " + e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
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
