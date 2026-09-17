using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using VDGS;

/// <summary>
/// Fly a camera along a straight line and save every frame, so a level-of-detail switch
/// can be measured instead of described.
///
/// Popping is a frame-to-frame event: the picture is stable, one selection tick lands, and
/// a whole leaf changes at once. Watching for it by eye finds the big ones and misses the
/// rest, and says nothing about whether a change made it better. Rendering a fixed path
/// and subtracting consecutive frames turns it into a number per frame - the spikes ARE
/// the pops, and their height is how visible they are.
///
/// The scene is loaded once and the path is walked in-process, because loading a 17M-splat
/// streamed SOG costs 40 seconds and one frame per process would take all day.
///
///   Unity -batchmode -quit -projectPath unity/VDGSBundler \
///         -executeMethod RenderSweep.Run -vdgsScene &lt;dir&gt; -vdgsOut &lt;dir&gt; \
///         -vdgsFrom x,y,z -vdgsTo x,y,z [-vdgsSteps 60] [-vdgsSize 512] [-vdgsFov 60]
///
/// -vdgsOrbit cx,cy,cz,radius circles that point instead, looking inward - the path a
/// drone actually flies around a subject, and the one that crosses the most band edges.
///
/// Note: NOT -nographics. Rendering needs a real graphics device.
/// </summary>
public static class RenderSweep
{
    public static void Run()
    {
        try
        {
            var scene = Arg("-vdgsScene");
            var outDir = Arg("-vdgsOut");
            if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(outDir))
                throw new System.Exception("usage: -vdgsScene <dir> -vdgsOut <dir> -vdgsFrom x,y,z -vdgsTo x,y,z");

            int steps = ParseInt("-vdgsSteps", 60);
            int size = ParseInt("-vdgsSize", 512);
            float fov = ParseFloat("-vdgsFov", 60f);
            var from = ParseVec("-vdgsFrom", new Vector3(0, 0, -100));
            var to = ParseVec("-vdgsTo", Vector3.zero);
            var fwd = (to - from).normalized;
            var orbit = Arg("-vdgsOrbit");

            Sweep(scene, outDir, steps, size, fov, from, to, fwd, orbit);
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[VDGS] " + e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    private static void Sweep(string sceneDir, string outDir, int steps, int size, float fov,
                              Vector3 from, Vector3 to, Vector3 fwd, string orbit)
    {
        bool isSog = sceneDir.EndsWith(".sog", System.StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(sceneDir, "lod-meta.json"))
            || (File.Exists(Path.Combine(sceneDir, "meta.json"))
                && File.ReadAllText(Path.Combine(sceneDir, "meta.json")).Contains("\"means\""));
        string error;
        var data = sceneDir.EndsWith(".ply", System.StringComparison.OrdinalIgnoreCase)
            ? PlyLoader.Load(sceneDir, out error)
            : isSog ? SogLoader.Load(sceneDir, out error)
                    : SplatData.Load(sceneDir, out error);
        if (data == null) throw new System.Exception("load failed: " + error);

        var go = new GameObject("VDGS_Sweep");
        var r = go.AddComponent<SplatRenderer>();
        r.m_ShaderSplats = Shader.Find("Gaussian Splatting/Render Splats");
        r.m_ShaderComposite = Shader.Find("Hidden/Gaussian Splatting/Composite");
        r.m_CSSplatUtilities = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/VDGS/Shaders/SplatUtilities.compute");
        if (r.m_ShaderSplats == null || r.m_CSSplatUtilities == null)
            throw new System.Exception("shaders not found in Assets/VDGS/Shaders");
        r.LodDetail = ParseFloat("-vdgsLodDetail", r.LodDetail);
        r.LodBudget = ParseInt("-vdgsLodBudget", (int)r.LodBudget);
        r.LodBandJitter = ParseFloat("-vdgsLodJitter", r.LodBandJitter);
        go.transform.localScale = Vector3.one * ParseFloat("-vdgsScale", 1f);
        r.SetData(data);

        var camGo = new GameObject("VDGS_SweepCam");
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = fov;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 10000f;
        camGo.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);

        Vector3 centre = Vector3.zero;
        float radius = 0f;
        if (!string.IsNullOrEmpty(orbit))
        {
            var o = orbit.Split(',');
            if (o.Length != 4) throw new System.Exception("-vdgsOrbit wants cx,cy,cz,radius");
            centre = new Vector3(float.Parse(o[0], CultureInfo.InvariantCulture),
                                 float.Parse(o[1], CultureInfo.InvariantCulture),
                                 float.Parse(o[2], CultureInfo.InvariantCulture));
            radius = float.Parse(o[3], CultureInfo.InvariantCulture);
        }

        var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
        cam.targetTexture = rt;
        var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
        Directory.CreateDirectory(outDir);

        for (int i = 0; i < steps; i++)
        {
            if (radius > 0f)
            {
                float t = steps == 1 ? 0f : i / (float)steps * 2f * Mathf.PI;
                camGo.transform.position = centre + new Vector3(Mathf.Sin(t) * radius, from.y - centre.y, Mathf.Cos(t) * radius);
                camGo.transform.rotation = Quaternion.LookRotation(centre - camGo.transform.position, Vector3.up);
            }
            else
            {
                camGo.transform.position = Vector3.Lerp(from, to, steps == 1 ? 0f : i / (float)(steps - 1));
            }

            // Two images per position, from the SAME camera: "before" still holds the
            // previous position's levels, "after" re-picks them. Subtracting the two
            // isolates the switch - comparing consecutive frames instead measures the
            // camera's own motion, which at any useful speed drowns the pop completely.
            Shot(cam, rt, tex, outDir, string.Format("b{0:000}", i));
            r.RefreshLod(cam);
            Shot(cam, rt, tex, outDir, string.Format("a{0:000}", i));

            var per = r.LodActivePerLevel;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[VDGS] SWEEP {0:000} pos {1:0.0},{2:0.0},{3:0.0} lod={4}",
                i, camGo.transform.position.x, camGo.transform.position.y, camGo.transform.position.z,
                per == null ? "none" : string.Join("/", per)));
        }

        Object.DestroyImmediate(tex);
        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(camGo);
        Object.DestroyImmediate(go);
    }

    private static void Shot(Camera cam, RenderTexture rt, Texture2D tex, string outDir, string name)
    {
        // Twice: the sort is queued alongside the draw, and a stale order renders
        // plausibly rather than obviously wrong.
        cam.Render();
        cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(Path.Combine(outDir, name + ".png"), tex.EncodeToPNG());
    }

    private static Vector3 ParseVec(string flag, Vector3 fallback)
    {
        var s = Arg(flag);
        if (string.IsNullOrEmpty(s)) return fallback;
        var p = s.Split(',');
        if (p.Length != 3) return fallback;
        return new Vector3(
            float.Parse(p[0], CultureInfo.InvariantCulture),
            float.Parse(p[1], CultureInfo.InvariantCulture),
            float.Parse(p[2], CultureInfo.InvariantCulture));
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
        return float.TryParse(Arg(flag), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static int ParseInt(string flag, int fallback)
    {
        return int.TryParse(Arg(flag), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
