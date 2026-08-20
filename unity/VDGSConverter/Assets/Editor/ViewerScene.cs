using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds a scene that shows every converted capture, so they can be inspected on a
/// Mac without going near VelociDrone.
///
/// Useful for judging a capture before shipping it to the game: scale, floaters and
/// reconstruction quality are all far easier to see with a free-flying editor camera
/// than by loading the sim, finding a track and flying to the right spot.
///
///   Unity -batchmode -quit -projectPath unity/VDGSConverter \
///         -executeMethod ViewerScene.Build
/// </summary>
public static class ViewerScene
{
    private const string ScenePath = "Assets/Viewer.unity";
    private const string AssetDir = "Assets/GaussianAssets";

    [MenuItem("VDGS/Build Viewer Scene")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        var guids = AssetDatabase.FindAssets("t:GaussianSplatAsset", new[] { AssetDir });
        if (guids.Length == 0)
        {
            Debug.LogError("[VDGS] no GaussianSplatAsset under " + AssetDir);
            if (Application.isBatchMode) EditorApplication.Exit(1);
            return;
        }

        // Shaders live in the package; the renderer needs them wired up by hand because
        // nothing is instantiating a configured prefab here.
        var shaderSplats = FindShader("Gaussian Splatting/Render Splats");
        var shaderComposite = FindShader("Hidden/Gaussian Splatting/Composite");
        var shaderPoints = FindShader("Gaussian Splatting/Debug/Render Points");
        var shaderBoxes = FindShader("Gaussian Splatting/Debug/Render Boxes");
        var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Packages/org.nesnausk.gaussian-splatting/Shaders/SplatUtilities.compute");

        var assets = guids
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>)
            .Where(a => a != null)
            .OrderByDescending(a => a.splatCount)
            .ToList();

        float x = 0f;
        bool first = true;
        foreach (var asset in assets)
        {
            var go = new GameObject(asset.name);
            go.transform.position = new Vector3(x, 0f, 0f);

            var r = go.AddComponent<GaussianSplatRenderer>();
            r.m_Asset = asset;
            r.m_ShaderSplats = shaderSplats;
            r.m_ShaderComposite = shaderComposite;
            r.m_ShaderDebugPoints = shaderPoints;
            r.m_ShaderDebugBoxes = shaderBoxes;
            r.m_CSSplatUtilities = cs;

            // Attach the runtime collision loader if a collision.bin exists for this
            // capture. It is the plugin's own SplatCollision.cs, symlinked into this
            // project rather than copied, so pressing Play exercises exactly the code the
            // game will run - same format, same Mesh build, same off-thread bake, same
            // MeshCollider settings. Testing a separate implementation would prove nothing
            // about the one that ships.
            var splatDir = Path.Combine(RepoRoot(), "build", "splats", asset.name);
            if (File.Exists(Path.Combine(splatDir, "collision.bin")))
            {
                var probe = go.AddComponent<SplatCollisionProbe>();
                probe.splatDir = splatDir;
                Debug.Log($"[VDGS] {asset.name}: collision.bin found, probe attached");
            }

            // One at a time: several million splats at once makes the editor crawl and
            // the captures overlap into noise.
            go.SetActive(first);
            first = false;

            var size = asset.boundsMax - asset.boundsMin;
            Debug.Log($"[VDGS] {asset.name}: {asset.splatCount:N0} splats, " +
                      $"bounds {size.x:0.0} x {size.y:0.0} x {size.z:0.0}");

            // Space them out by their own width so nothing overlaps when enabled together.
            x += Mathf.Max(size.x, 5f) + 5f;
        }

        // Frame the first capture: the default camera at the origin usually sits inside it.
        var cam = Camera.main;
        if (cam != null && assets.Count > 0)
        {
            var a = assets[0];
            var center = (a.boundsMin + a.boundsMax) * 0.5f;
            var extent = (a.boundsMax - a.boundsMin).magnitude;
            cam.transform.position = center + new Vector3(0f, extent * 0.15f, -extent * 0.7f);
            cam.transform.LookAt(center);
            cam.farClipPlane = Mathf.Max(1000f, extent * 5f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.09f, 0.11f);
        }

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[VDGS] viewer scene written: {ScenePath} ({assets.Count} captures, " +
                  "only the largest enabled - toggle the others in the Hierarchy)");

        if (Application.isBatchMode) EditorApplication.Exit(0);
    }

    /// <summary>
    /// The repository root, from the project path. Application.dataPath points at
    /// unity/VDGSConverter/Assets, so the root is three levels up.
    /// </summary>
    private static string RepoRoot()
    {
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
    }

    private static Shader FindShader(string name)
    {
        var s = Shader.Find(name);
        if (s == null) Debug.LogWarning("[VDGS] shader not found: " + name);
        return s;
    }
}
