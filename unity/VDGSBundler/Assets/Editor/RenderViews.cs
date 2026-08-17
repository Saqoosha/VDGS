using System.IO;
using UnityEditor;
using UnityEngine;
using VDGS;

/// <summary>
/// Renders a converted splat scene from fixed orthographic directions, so the result
/// can be compared against the same views in SuperSplat.
///
/// Orthographic on purpose: it removes perspective, so the two images line up even
/// when the cameras are not in exactly the same place. That makes the comparison
/// mechanical - mirroring, upside-down and missing density all show up as measurable
/// differences rather than a judgement call. Every orientation bug hit so far was
/// found by eye, late, after a slow round trip through the game.
///
///   Unity -batchmode -quit -projectPath unity/VDGSBundler \
///         -executeMethod RenderViews.Run -vdgsScene &lt;dir&gt; -vdgsOut &lt;dir&gt;
///
/// Note: NOT -nographics. Rendering needs a real graphics device.
/// </summary>
public static class RenderViews
{
    private static int kSize = 1024;

    // The sort is queued into the same command buffer as the draw, so one Render()
    // should be enough - but that is an assumption about upstream's ordering, and a
    // splat scene renders plausibly wrong when the order is stale. Capturing after a
    // second pass costs nothing and removes the doubt.
    private const int kWarmupFrames = 1;

    // Narrow enough to read as orthographic, wide enough that the perspective
    // Jacobian in the splat shader stays well conditioned.
    private const float kNarrowFov = 4f;

    // Matches SuperSplat's view cube: the six axis-aligned directions.
    private static readonly (string name, Vector3 dir, Vector3 up)[] kViews =
    {
        ("front",  new Vector3(0, 0, -1), Vector3.up),
        ("back",   new Vector3(0, 0,  1), Vector3.up),
        ("left",   new Vector3(-1, 0, 0), Vector3.up),
        ("right",  new Vector3( 1, 0, 0), Vector3.up),
        ("top",    new Vector3(0,  1, 0), Vector3.forward),
        ("bottom", new Vector3(0, -1, 0), Vector3.forward),
    };

    [MenuItem("VDGS/Render Comparison Views")]
    public static void RunMenu() => Render(
        Path.GetFullPath("../../build/splats/playroom"),
        Path.GetFullPath("../../build/views"));

    public static void Run()
    {
        var scene = GetArg("-vdgsScene");
        var outDir = GetArg("-vdgsOut");
        // SuperSplat is compared against on a retina display, so matching it means
        // being able to render past 1024.
        if (int.TryParse(GetArg("-vdgsSize") ?? "", out var size) && size >= 64)
            kSize = size;
        if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(outDir))
        {
            Debug.LogError("[VDGS] usage: -vdgsScene <dir> -vdgsOut <dir>");
            if (Application.isBatchMode) EditorApplication.Exit(1);
            return;
        }

        try
        {
            Render(scene, outDir);
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[VDGS] " + e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    private static void Render(string sceneDir, string outDir)
    {
        Directory.CreateDirectory(outDir);

        var data = SplatData.Load(sceneDir, out var error);
        if (data == null)
            throw new System.Exception("load failed: " + error);

        Debug.Log($"[VDGS] {Path.GetFileName(sceneDir)}: {data.SplatCount:N0} splats, " +
                  $"bounds {data.BoundsMin} .. {data.BoundsMax}");

        var go = new GameObject("VDGS_Render");
        var r = go.AddComponent<SplatRenderer>();
        r.m_ShaderSplats = Shader.Find("Gaussian Splatting/Render Splats");
        r.m_ShaderComposite = Shader.Find("Hidden/Gaussian Splatting/Composite");
        r.m_CSSplatUtilities = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/VDGS/Shaders/SplatUtilities.compute");

        if (r.m_ShaderSplats == null || r.m_CSSplatUtilities == null)
            throw new System.Exception("shaders not found in Assets/VDGS/Shaders");

        r.SetData(data);

        var camGo = new GameObject("VDGS_Cam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.1f, 0.1f, 0.12f, 1f);

        var centre = (data.BoundsMin + data.BoundsMax) * 0.5f;
        var extent = data.BoundsMax - data.BoundsMin;
        // Fit the longest axis with a small margin so nothing clips at the frame edge.
        var radius = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z)) * 0.5f * 1.1f;

        // Deliberately NOT an orthographic camera, however much these views want to be
        // orthographic. The splat shader projects each gaussian's covariance with the
        // perspective Jacobian:
        //
        //     focal = screenParams.x * matrixP._m00 / 2;
        //     J = { focal/z, 0, -focal*x/z^2, ... }
        //
        // Under an orthographic projection _m00 is not a tangent-of-fov and the 1/z
        // terms are meaningless, so every splat gets the wrong screen-space size and a
        // spurious shear. It still renders a recognisable scene, which is the trap - it
        // just renders it softer than it should be, and it was blamed on the data.
        //
        // A very narrow field of view from far away is orthographic in all but name,
        // and keeps the shader's arithmetic valid.
        cam.orthographic = false;
        cam.fieldOfView = kNarrowFov;
        var distance = radius / Mathf.Tan(kNarrowFov * 0.5f * Mathf.Deg2Rad);
        cam.nearClipPlane = Mathf.Max(0.01f, distance - radius * 2f);
        cam.farClipPlane = distance + radius * 4f;

        var rt = new RenderTexture(kSize, kSize, 24, RenderTextureFormat.ARGB32)
        { antiAliasing = 1 };
        cam.targetTexture = rt;

        foreach (var (name, dir, up) in kViews)
        {
            camGo.transform.position = centre - dir * distance;
            camGo.transform.rotation = Quaternion.LookRotation(dir, up);

            for (int i = 0; i < kWarmupFrames; i++)
                cam.Render();
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(kSize, kSize, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, kSize, kSize), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            var path = Path.Combine(outDir, name + ".png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Debug.Log("[VDGS] wrote " + path);
        }

        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(camGo);
        Object.DestroyImmediate(go);
    }

    private static string GetArg(string flag)
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, System.StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
