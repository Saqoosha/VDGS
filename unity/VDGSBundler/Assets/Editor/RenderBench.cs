using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using VDGS;
using Debug = UnityEngine.Debug;

/// <summary>
/// Time a splat scene by rendering it repeatedly, so a format change can be judged
/// before shipping hundreds of megabytes to the game host and waiting for a track to be
/// open.
///
/// The Mac's GPU is not the machine that matters, so the absolute milliseconds do not
/// transfer. The RATIO between two scenes does, and that is what a format comparison
/// needs: same resolution, same camera, same code path, only the data differs.
///
/// Camera.Render() returns before the GPU has finished, so timing it alone measures
/// almost nothing. Reading the target texture back forces a sync, which makes each
/// iteration a real end-to-end frame. The readback costs the same for every scene at a
/// given resolution, so it cancels out of the comparison - and running with no scene at
/// all measures exactly that floor.
///
///   Unity -batchmode -quit -projectPath unity/VDGSBundler \
///         -executeMethod RenderBench.Run -vdgsScene &lt;dir&gt; -vdgsFrames 120
///
/// Pass -vdgsScene none to measure the empty-frame floor.
/// </summary>
public static class RenderBench
{
    public static void Run()
    {
        try
        {
            var scene = Arg("-vdgsScene") ?? "none";
            int size = ParseInt("-vdgsSize", 1024);
            int frames = ParseInt("-vdgsFrames", 120);
            int warmup = ParseInt("-vdgsWarmup", 30);
            // Framing the whole capture culls nothing, so it cannot measure culling at
            // all. -vdgsInside puts the camera in the middle of the scene with an FPV-ish
            // field of view, which is where the drone actually is.
            bool inside = ParseInt("-vdgsInside", 0) != 0;
            int cull = ParseInt("-vdgsCull", 1);
            float fov = ParseInt("-vdgsFov", inside ? 120 : 60);

            Bench(scene, size, frames, warmup, inside, cull, fov);
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[VDGS] " + e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    private static void Bench(string sceneDir, int size, int frames, int warmup,
                              bool inside, int cull, float fov)
    {
        GameObject go = null;
        var label = "empty";
        var splats = 0;
        var centre = Vector3.zero;
        var radius = 5f;

        if (sceneDir != "none")
        {
            // A .ply goes through the runtime loader, a directory through the on-disk one.
            // -vdgsPlyNoMirror keeps the loader's transform identical to the offline
            // converter's, which is how the two are compared.
            var data = sceneDir.EndsWith(".ply", System.StringComparison.OrdinalIgnoreCase)
                ? PlyLoader.Load(sceneDir, out var error, Arg("-vdgsPlyNoMirror") == null)
                : SplatData.Load(sceneDir, out error);
            if (data == null) throw new System.Exception("load failed: " + error);

            label = Path.GetFileName(sceneDir);
            splats = data.SplatCount;
            centre = (data.BoundsMin + data.BoundsMax) * 0.5f;
            var extent = data.BoundsMax - data.BoundsMin;
            radius = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z)) * 0.5f;

            go = new GameObject("VDGS_Bench");
            var r = go.AddComponent<SplatRenderer>();
            r.m_ShaderSplats = Shader.Find("Gaussian Splatting/Render Splats");
            r.m_ShaderComposite = Shader.Find("Hidden/Gaussian Splatting/Composite");
            r.m_CSSplatUtilities = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/VDGS/Shaders/SplatUtilities.compute");
            if (r.m_ShaderSplats == null || r.m_CSSplatUtilities == null)
                throw new System.Exception("shaders not found in Assets/VDGS/Shaders");
            // Sorting every frame is the suspected cost centre; -vdgsSortNth makes its
            // share measurable. A high value renders with a stale order, which is wrong
            // to look at but exactly right for attributing time.
            r.m_SortNthFrame = ParseInt("-vdgsSortNth", 1);
            r.m_FrustumCulling = cull != 0;
            r.m_CullMargin = ParseFloat("-vdgsCullMargin", r.m_CullMargin);
            r.SetData(data);
        }

        var camGo = new GameObject("VDGS_BenchCam");
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = false;
        // Wide enough that the whole capture is on screen: the point is to make every
        // splat do work, not to find a flattering angle.
        cam.fieldOfView = fov;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = radius * 20f + 100f;
        var dist = inside ? 0f : radius / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 1.1f;
        camGo.transform.position = centre - Vector3.forward * dist;
        camGo.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);

        var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
        cam.targetTexture = rt;
        var tex = new Texture2D(1, 1, TextureFormat.RGB24, false);

        for (int i = 0; i < warmup; i++) Frame(cam, rt, tex, size);

        var times = new double[frames];
        var sw = new Stopwatch();
        for (int i = 0; i < frames; i++)
        {
            sw.Restart();
            Frame(cam, rt, tex, size);
            sw.Stop();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }

        System.Array.Sort(times);
        double sum = 0;
        foreach (var t in times) sum += t;
        var mean = sum / frames;
        var median = times[frames / 2];
        var p10 = times[frames / 10];

        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "[VDGS] BENCH {0}  splats={1}  {2}x{2}  frames={3}  sortNth={7}  cull={8}  view={9}   " +
            "mean {4:0.00} ms   median {5:0.00} ms   best10% {6:0.00} ms",
            label, splats, size, frames, mean, median, p10, ParseInt("-vdgsSortNth", 1),
            cull, inside ? "inside" : "whole"));

        Object.DestroyImmediate(tex);
        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(camGo);
        if (go != null) Object.DestroyImmediate(go);
    }

    /// <summary>
    /// One end-to-end frame: draw, then read a single pixel back so the GPU actually
    /// finishes before the stopwatch stops.
    ///
    /// One pixel, not the whole target. Reading the full 1024x1024 back costs nothing
    /// worth noticing on a Mac's unified memory, but across PCIe to a discrete card it
    /// dominates: an empty frame measured 17.55 ms on the RTX 3060, slower than actually
    /// rendering two million splats. A 1x1 read still forces the same sync and transfers
    /// four bytes.
    /// </summary>
    private static void Frame(Camera cam, RenderTexture rt, Texture2D tex, int size)
    {
        cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
    }

    private static string Arg(string flag)
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, System.StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static float ParseFloat(string flag, float fallback)
    {
        return float.TryParse(Arg(flag), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static int ParseInt(string flag, int fallback)
    {
        return int.TryParse(Arg(flag), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
