using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Converts a PLY/SPZ splat file into the flat on-disk layout the injected VDGS plugin
/// reads (meta.json + five .bin files). See AGENTS.md for the format.
///
/// Upstream only exposes conversion through an EditorWindow, so the window's private
/// state is driven by reflection rather than duplicating a thousand lines of encoding
/// logic that would then have to be kept in sync.
///
/// Batch usage:
///   Unity -batchmode -quit -projectPath unity/VDGSConverter \
///         -executeMethod PlyExporter.Run \
///         -vdgsInput /path/to/scene.ply -vdgsOutput /path/to/out -vdgsQuality Medium
///         [-vdgsShFormat Cluster16k]   # compress SH only, keep geometry exact
/// </summary>
public static class PlyExporter
{
    private const string ImportFolder = "Assets/GaussianAssets";

    [MenuItem("VDGS/Export Splat Asset to VDGS format")]
    public static void ExportSelected()
    {
        var asset = Selection.activeObject as GaussianSplatAsset;
        if (asset == null)
        {
            Debug.LogError("[VDGS] select a GaussianSplatAsset first");
            return;
        }
        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "../../build/splats", asset.name);
        Export(asset, outDir);
    }

    public static void Run()
    {
        try
        {
            var input = GetArg("-vdgsInput");
            var output = GetArg("-vdgsOutput");
            var quality = GetArg("-vdgsQuality") ?? "Medium";

            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output))
                throw new Exception("usage: -vdgsInput <ply> -vdgsOutput <dir> [-vdgsQuality Medium] [-vdgsShFormat Cluster16k]");
            if (!File.Exists(input))
                throw new Exception("input not found: " + input);

            Debug.Log($"[VDGS] converting {input} (quality {quality})");

            var asset = Convert(input, quality, GetArg("-vdgsShFormat"));
            if (asset == null)
                throw new Exception("conversion produced no asset");

            Export(asset, output);

            Debug.Log("[VDGS] done");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[VDGS] " + e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    /// <summary>Drives GaussianSplatAssetCreator's private conversion path.</summary>
    /// <summary>
    /// Drives the conversion, optionally overriding the SH format on its own.
    ///
    /// Spherical harmonics dominate everything else: at Float32 they are 192 bytes per
    /// splat against 12 for position, and 81% of drjohnson's 750 MB. Rendering cost
    /// tracks bytes per splat almost linearly (see docs/performance.md), so compressing
    /// SH alone is the largest available win.
    ///
    /// The quality presets cannot express it: SH clustering only switches on at Low and
    /// VeryLow, which drag position, scale and colour down with it. Applying the preset
    /// first and then overwriting one field gives full geometric precision with a
    /// palette-compressed SH, which is the combination actually wanted.
    /// </summary>
    private static GaussianSplatAsset Convert(string plyPath, string quality, string shFormat)
    {
        var t = typeof(GaussianSplatAssetCreator);
        var win = ScriptableObject.CreateInstance<GaussianSplatAssetCreator>();

        SetField(t, win, "m_InputFile", plyPath);
        SetField(t, win, "m_OutputFolder", ImportFolder);
        SetField(t, win, "m_ImportCameras", false);

        var qualityType = t.GetNestedType("DataQuality", BindingFlags.NonPublic);
        if (qualityType == null)
            throw new Exception("DataQuality enum not found - upstream layout changed");
        SetField(t, win, "m_Quality", Enum.Parse(qualityType, quality, true));

        // The window normally applies quality->format mapping from its OnGUI; call it
        // directly so the formats are not left at their default values.
        var apply = t.GetMethod("ApplyQualityLevel", BindingFlags.NonPublic | BindingFlags.Instance);
        apply?.Invoke(win, null);

        // After the preset, so it wins.
        if (!string.IsNullOrEmpty(shFormat))
        {
            var shType = typeof(GaussianSplatAsset).GetNestedType("SHFormat");
            if (shType == null)
                throw new Exception("SHFormat enum not found - upstream layout changed");
            SetField(t, win, "m_FormatSH", Enum.Parse(shType, shFormat, true));
            Debug.Log("[VDGS] SH format overridden to " + shFormat);
        }

        Directory.CreateDirectory(ImportFolder);
        AssetDatabase.Refresh();

        var before = new HashSet<string>(AssetDatabase.FindAssets("t:GaussianSplatAsset", new[] { ImportFolder }));

        var create = t.GetMethod("CreateAsset", BindingFlags.NonPublic | BindingFlags.Instance);
        if (create == null)
            throw new Exception("CreateAsset() not found - upstream layout changed");
        create.Invoke(win, null);

        AssetDatabase.Refresh();

        foreach (var guid in AssetDatabase.FindAssets("t:GaussianSplatAsset", new[] { ImportFolder }))
        {
            if (before.Contains(guid)) continue;
            var path = AssetDatabase.GUIDToAssetPath(guid);
            Debug.Log("[VDGS] created " + path);
            return AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(path);
        }

        // Re-running on the same input overwrites rather than adds; fall back to newest.
        GaussianSplatAsset newest = null;
        var newestTime = DateTime.MinValue;
        foreach (var guid in AssetDatabase.FindAssets("t:GaussianSplatAsset", new[] { ImportFolder }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var time = File.GetLastWriteTimeUtc(path);
            if (time <= newestTime) continue;
            newestTime = time;
            newest = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(path);
        }
        return newest;
    }

    /// <summary>Writes meta.json + the five raw buffers the plugin expects.</summary>
    public static void Export(GaussianSplatAsset asset, string outDir)
    {
        Directory.CreateDirectory(outDir);

        WriteBytes(outDir, "chunk.bin", asset.chunkData);
        WriteBytes(outDir, "pos.bin", asset.posData);
        WriteBytes(outDir, "other.bin", asset.otherData);
        WriteBytes(outDir, "color.bin", asset.colorData);
        WriteBytes(outDir, "sh.bin", asset.shData);

        // Hand-rolled rather than JsonUtility: the plugin reads plain float arrays and
        // enum *names*, which JsonUtility would not produce for Vector3/enum fields.
        var json =
            "{\n" +
            "  \"formatVersion\": " + asset.formatVersion + ",\n" +
            "  \"splatCount\": " + asset.splatCount + ",\n" +
            "  \"boundsMin\": " + Vec(asset.boundsMin) + ",\n" +
            "  \"boundsMax\": " + Vec(asset.boundsMax) + ",\n" +
            "  \"posFormat\": \"" + asset.posFormat + "\",\n" +
            "  \"scaleFormat\": \"" + asset.scaleFormat + "\",\n" +
            "  \"colorFormat\": \"" + asset.colorFormat + "\",\n" +
            "  \"shFormat\": \"" + asset.shFormat + "\"\n" +
            "}\n";
        File.WriteAllText(Path.Combine(outDir, "meta.json"), json);

        Debug.Log($"[VDGS] exported '{asset.name}' splats={asset.splatCount} -> {outDir}");
    }

    private static void WriteBytes(string dir, string name, TextAsset data)
    {
        var path = Path.Combine(dir, name);
        if (data == null)
        {
            // chunk.bin is legitimately absent for unchunked scenes.
            if (File.Exists(path)) File.Delete(path);
            Debug.Log("[VDGS]   " + name + ": (none)");
            return;
        }
        File.WriteAllBytes(path, data.bytes);
        Debug.Log("[VDGS]   " + name + ": " + data.bytes.Length + " bytes");
    }

    private static string Vec(Vector3 v) =>
        "[" + v.x.ToString("R") + ", " + v.y.ToString("R") + ", " + v.z.ToString("R") + "]";

    private static void SetField(Type t, object target, string name, object value)
    {
        var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) throw new Exception("field not found: " + name);
        f.SetValue(target, value);
    }

    private static string GetArg(string flag)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
