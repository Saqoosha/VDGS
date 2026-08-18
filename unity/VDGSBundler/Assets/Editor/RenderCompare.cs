using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using VDGS;

/// <summary>
/// Renders one frame from an explicitly given camera, so the same view can be produced
/// by a second, independent renderer and the two images subtracted.
///
/// The point is to stop grading our renderer against itself. Every orientation and
/// format bug so far survived because the only check was "does this look like a room",
/// and it always did. A reference renderer fed the same .ply from the same camera turns
/// that into a number.
///
/// The camera is specified the way a WebGL splat viewer specifies it - a focal length in
/// pixels rather than a field of view - because that is the harder one to fake:
///
///     fovY = 2 * atan(height / (2 * focal))
///
///   Unity -batchmode -quit -projectPath unity/VDGSBundler \
///         -executeMethod RenderCompare.Run \
///         -vdgsScene &lt;dir&gt; -vdgsOutFile &lt;png&gt; \
///         -vdgsCamPos x,y,z -vdgsCamFwd x,y,z -vdgsCamUp x,y,z \
///         -vdgsFocal 1160 -vdgsSize 1024
///
/// Note: NOT -nographics, and never an orthographic camera - the splat shader projects
/// each gaussian's covariance with a perspective Jacobian and quietly produces softer,
/// wrong-sized splats under an orthographic projection.
/// </summary>
public static class RenderCompare
{
    public static void Run()
    {
        try
        {
            var scene = Required("-vdgsScene");
            var outFile = Required("-vdgsOutFile");
            int size = ParseInt("-vdgsSize", 1024);
            float focal = ParseFloat("-vdgsFocal", 0f);
            int cull = ParseInt("-vdgsCull", 1);

            var pos = ParseVec("-vdgsCamPos", Vector3.zero);
            var fwd = ParseVec("-vdgsCamFwd", Vector3.forward).normalized;
            var up = ParseVec("-vdgsCamUp", Vector3.up).normalized;

            // A focal length in pixels is what a WebGL viewer takes; convert it to the
            // vertical field of view Unity wants. Falling back to a field of view given
            // directly keeps the script usable on its own.
            float fov = focal > 0f
                ? 2f * Mathf.Atan(size / (2f * focal)) * Mathf.Rad2Deg
                : ParseFloat("-vdgsFov", 40f);

            Render(scene, outFile, size, pos, fwd, up, fov, cull);
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[VDGS] " + e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    private static void Render(string sceneDir, string outFile, int size,
                               Vector3 pos, Vector3 fwd, Vector3 up, float fov, int cull)
    {
        var data = SplatData.Load(sceneDir, out var error);
        if (data == null) throw new System.Exception("load failed: " + error);

        Debug.Log($"[VDGS] {Path.GetFileName(sceneDir)}: {data.SplatCount:N0} splats, " +
                  $"fov {fov:0.0000} deg, {size}x{size}");

        var go = new GameObject("VDGS_Compare");
        var r = go.AddComponent<SplatRenderer>();
        r.m_ShaderSplats = Shader.Find("Gaussian Splatting/Render Splats");
        r.m_ShaderComposite = Shader.Find("Hidden/Gaussian Splatting/Composite");
        r.m_CSSplatUtilities = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/VDGS/Shaders/SplatUtilities.compute");
        if (r.m_ShaderSplats == null || r.m_CSSplatUtilities == null)
            throw new System.Exception("shaders not found in Assets/VDGS/Shaders");
        // Culling must not change a single pixel; -vdgsCull 0 is how that gets proven.
        r.m_FrustumCulling = cull != 0;
        r.m_CullMargin = ParseFloat("-vdgsCullMargin", r.m_CullMargin);
        r.SetData(data);

        var camGo = new GameObject("VDGS_CompareCam");
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = false;
        cam.fieldOfView = fov;
        cam.clearFlags = CameraClearFlags.SolidColor;
        // Black, so the reference renderer's background can be matched exactly and the
        // difference image is not dominated by whatever each tool clears to.
        cam.backgroundColor = Color.black;
        cam.nearClipPlane = 0.2f;   // matches the WebGL viewer's znear
        cam.farClipPlane = 200f;    // and its zfar
        camGo.transform.position = pos;
        camGo.transform.rotation = Quaternion.LookRotation(fwd, up);

        var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32)
        { antiAliasing = 1 };
        cam.targetTexture = rt;

        // One warm-up pass: the sort is queued alongside the draw, and a stale order
        // renders plausibly rather than obviously wrong.
        cam.Render();
        cam.Render();

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)));
        File.WriteAllBytes(outFile, tex.EncodeToPNG());
        Debug.Log("[VDGS] wrote " + outFile);

        Object.DestroyImmediate(tex);
        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(camGo);
        Object.DestroyImmediate(go);
    }

    private static string Arg(string flag)
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, System.StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static string Required(string flag)
    {
        var v = Arg(flag);
        if (string.IsNullOrEmpty(v)) throw new System.Exception("missing " + flag);
        return v;
    }

    private static int ParseInt(string flag, int fallback)
    {
        return int.TryParse(Arg(flag), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static float ParseFloat(string flag, float fallback)
    {
        return float.TryParse(Arg(flag), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static Vector3 ParseVec(string flag, Vector3 fallback)
    {
        var s = Arg(flag);
        if (string.IsNullOrEmpty(s)) return fallback;
        var p = s.Split(',');
        if (p.Length != 3) throw new System.Exception(flag + " needs x,y,z");
        return new Vector3(
            float.Parse(p[0], CultureInfo.InvariantCulture),
            float.Parse(p[1], CultureInfo.InvariantCulture),
            float.Parse(p[2], CultureInfo.InvariantCulture));
    }
}
