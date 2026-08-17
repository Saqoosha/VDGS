using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Packs the Gaussian Splatting shaders into an AssetBundle the injected plugin can
/// load at runtime. Only shaders go in here - splat data is raw binary read straight
/// off disk, and the C# lives in the BepInEx plugin, so neither belongs in a bundle.
///
/// Must be built with the same Unity version as the game (2021.3.45f2) or the
/// serialized shader format will not load.
/// </summary>
public static class BuildBundles
{
    private const string BundleName = "vdgs-shaders";
    private const string ShaderDir = "Assets/VDGS/Shaders";

    [MenuItem("VDGS/Build Shader Bundle (Windows)")]
    public static void BuildWindows() => Build(BuildTarget.StandaloneWindows64, "Windows");

    [MenuItem("VDGS/Build Shader Bundle (macOS)")]
    public static void BuildMac() => Build(BuildTarget.StandaloneOSX, "OSX");

    /// <summary>
    /// The splat shaders declare `#pragma use_dxc` and `#pragma require wavebasic/waveballot`.
    /// Those only compile for D3D12/Vulkan/Metal - under the project's default D3D11 target
    /// they silently build to an unsupported (empty) shader, and the bundle loads fine but
    /// every shader reports isSupported=false. Set the API before building, not after.
    /// </summary>
    [MenuItem("VDGS/Set Graphics APIs (D3D12)")]
    public static void SetGraphicsApis()
    {
        // D3D12, and this project must be built on Windows.
        //
        // The splat shaders use `#pragma use_dxc` + `#pragma require wavebasic/waveballot`,
        // which a macOS Editor refuses to compile for a D3D target ("can only use DXC to
        // target D3D from the Windows Editor") - it emits unsupported shaders without
        // failing the build. Targeting Vulkan does compile on macOS, but VelociDrone itself
        // ships no Vulkan shaders, so -force-vulkan leaves the game with a black screen
        // ("Forced GfxDevice 'Vulkan' was not built from editor"). D3D12 built on Windows
        // is the only combination where both the game and the splats render.
        Apply(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12 });
        Apply(BuildTarget.StandaloneOSX, new[] { GraphicsDeviceType.Metal });
        AssetDatabase.SaveAssets();

        if (Application.isBatchMode)
            EditorApplication.Exit(0);
    }

    private static void Apply(BuildTarget target, GraphicsDeviceType[] apis)
    {
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        var current = PlayerSettings.GetGraphicsAPIs(target);
        if (current == null || !current.SequenceEqual(apis))
        {
            PlayerSettings.SetGraphicsAPIs(target, apis);
            Debug.Log($"[VDGS] {target} graphics APIs -> {string.Join(", ", apis)}");
        }
        else
        {
            Debug.Log($"[VDGS] {target} graphics APIs already {string.Join(", ", apis)}");
        }
    }

    private static void Build(BuildTarget target, string label)
    {
        AssignBundleNames();

        // Directory.GetCurrentDirectory() in batch mode is the shell's cwd, NOT the
        // project path, so an explicit -vdgsOut is the only reliable way to place the
        // bundle when the build is driven from elsewhere.
        var outDir = GetArg("-vdgsOut")
                     ?? Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "..", "..", "build", "bundles", label);
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);

        Debug.Log($"[VDGS] building '{BundleName}' for {target} -> {outDir}");

        var manifest = BuildPipeline.BuildAssetBundles(
            outDir,
            BuildAssetBundleOptions.None,
            target);

        if (manifest == null)
        {
            Debug.LogError("[VDGS] BuildAssetBundles returned null");
            EditorApplication.Exit(1);
            return;
        }

        foreach (var name in manifest.GetAllAssetBundles())
        {
            var path = Path.Combine(outDir, name);
            Debug.Log($"[VDGS] built {name}  {new FileInfo(path).Length} bytes");
        }

        // Batch-mode invocations need an explicit success exit or Unity returns its own code.
        if (Application.isBatchMode)
            EditorApplication.Exit(0);
    }

    private static string GetArg(string flag)
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, System.StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void AssignBundleNames()
    {
        // .hlsl includes are pulled in by the shader compiler, not referenced as assets,
        // so tagging the .shader/.compute files is enough.
        var guids = AssetDatabase.FindAssets("t:Shader t:ComputeShader", new[] { ShaderDir });
        var count = 0;

        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) continue;
            if (importer.assetBundleName != BundleName)
                importer.SetAssetBundleNameAndVariant(BundleName, string.Empty);
            // Shader variants are cached per graphics API; a stale cache from a previous
            // D3D11 build would be reused and stay unsupported.
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            count++;
            Debug.Log($"[VDGS] tagged {path}");
        }

        Debug.Log($"[VDGS] tagged {count} shader assets into '{BundleName}'");
        AssetDatabase.SaveAssets();
    }
}
