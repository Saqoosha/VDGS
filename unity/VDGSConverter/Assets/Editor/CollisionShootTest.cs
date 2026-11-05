using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Fires balls at collision shapes at flying speeds and records where each one ends up -
/// headless, at the game's 400 Hz and gravity. A measuring tool, not a gate: it writes a CSV
/// and the verdicts (tunnelled, snagged) are made from it offline.
///
///   Unity -batchmode -quit -nographics -projectPath unity/VDGSConverter \
///         -executeMethod CollisionShootTest.Run -vdgsShootDir &lt;dir&gt; -vdgsOut &lt;csv&gt;
///
/// The dir holds:
///   ground-deployed.collision.bin, ground-new.collision.bin   ground meshes (ply frame)
///   g24-deployed.collision.bin                                the shipped shell, cut to gate 24
///   g24-parts.json                                            capsules from tools/fit_parts.py
///   shots.json                                                start points and directions
///
/// Meshes go through the plugin's own SplatCollision (via SplatCollisionProbe), so the
/// mirror and the winding swap are the shipped ones. Capsules are placed here, mirrored in Y
/// the same way, because the plugin has no capsule loader yet.
/// </summary>
public static class CollisionShootTest
{
    private const float kStep = 0.0025f;                  // 400 Hz, as the game
    private static readonly Vector3 kGravity = new Vector3(0f, -10.78f, 0f);

    [Serializable] private class Cap { public float[] a; public float[] b; public float r; }
    [Serializable] private class Caps { public Cap[] capsules; }
    [Serializable] private class Shot { public float[] p; public float[] d; public float s; public float y; public int cap; }
    [Serializable] private class Shots { public Shot[] window; public Shot[] members; }

    private static Vector3 Ply(float[] v) { return new Vector3(v[0], -v[1], v[2]); }

    public static void Run()
    {
        var dir = Arg("-vdgsShootDir");
        var outPath = Arg("-vdgsOut");
        var ci = CultureInfo.InvariantCulture;
        // Both are project settings: set them, and put them back before exit, or Unity
        // saves them into ProjectSettings/DynamicsManager.asset.
        var wasMode = Physics.simulationMode; var wasGravity = Physics.gravity;
        Physics.simulationMode = SimulationMode.Script;
        Physics.gravity = kGravity;
        Debug.Log($"[VDGS] defaultContactOffset {Physics.defaultContactOffset}");

        var roots = new Dictionary<string, GameObject>();
        foreach (var name in new[] { "ground-deployed", "ground-new", "g24-deployed" })
        {
            var root = new GameObject(name);
            var log = new StringBuilder();
            // The ".ply" suffix makes the loader mirror in Y, exactly as for a ply capture.
            if (!SplatCollisionProbe.Attach(root.transform, Path.Combine(dir, name + ".ply"), log))
                throw new Exception("attach failed: " + name + "\n" + log);
            roots[name] = root;
            root.SetActive(false);
        }
        {
            var root = new GameObject("g24-parts");
            var caps = JsonUtility.FromJson<Caps>(File.ReadAllText(Path.Combine(dir, "g24-parts.json")));
            foreach (var c in caps.capsules)
            {
                Vector3 a = Ply(c.a), b = Ply(c.b);
                var go = new GameObject("capsule");
                go.transform.SetParent(root.transform, false);
                go.transform.position = (a + b) * 0.5f;
                go.transform.rotation = Quaternion.FromToRotation(Vector3.up, (b - a).normalized);
                var cc = go.AddComponent<CapsuleCollider>();
                cc.direction = 1;
                cc.radius = c.r;
                cc.height = (b - a).magnitude + 2f * c.r;
            }
            roots["g24-parts"] = root;
            root.SetActive(false);
            Debug.Log($"[VDGS] {caps.capsules.Length} CapsuleColliders");
        }

        var shots = JsonUtility.FromJson<Shots>(File.ReadAllText(Path.Combine(dir, "shots.json")));
        var csv = new StringBuilder("test,config,mode,radius,speed,i,sx,sy,sz,dx,dy,dz,ex,ey,ez,hit,cap,s,y\n");
        var modes = new[] { CollisionDetectionMode.Discrete, CollisionDetectionMode.ContinuousDynamic };
        var radii = new[] { 0.05f, 0.125f };

        // Ground: straight down at the surface a downward ray finds, from 0.5 m above it.
        var rng = new System.Random(1);
        var groundXZ = new List<Vector2>();
        for (int i = 0; i < 100; i++)
            groundXZ.Add(new Vector2((float)(-30 + rng.NextDouble() * 73), (float)(-2 + rng.NextDouble() * 60)));
        foreach (var cfg in new[] { "ground-deployed", "ground-new" })
        {
            roots[cfg].SetActive(true);
            Physics.SyncTransforms();
            foreach (var mode in modes)
            foreach (var r in radii)
            foreach (var v in new[] { 5f, 10f, 20f, 30f, 40f, 60f })
                for (int i = 0; i < groundXZ.Count; i++)
                {
                    var top = new Vector3(groundXZ[i].x, 30f, groundXZ[i].y);
                    if (!Physics.Raycast(top, Vector3.down, out var hit, 60f)) continue;
                    var start = hit.point + Vector3.up * (r + 0.5f);
                    var end = Fire(start, Vector3.down * v, r, mode, 0.25f, out var touched);
                    csv.AppendLine(string.Join(",", "ground", cfg, mode, r.ToString(ci), v.ToString(ci), i,
                        F(hit.point), "0,-1,0", F(end), touched ? 1 : 0, -1, 0, 0));
                }
            roots[cfg].SetActive(false);
        }

        // Gate 24: at each member, and a grid through the ladder plane.
        foreach (var cfg in new[] { "g24-deployed", "g24-parts" })
        {
            roots[cfg].SetActive(true);
            Physics.SyncTransforms();
            foreach (var mode in modes)
            foreach (var r in radii)
            {
                foreach (var v in new[] { 10f, 20f, 40f, 60f })
                    for (int i = 0; i < shots.members.Length; i++)
                    {
                        var sh = shots.members[i];
                        var d = Ply(sh.d).normalized;
                        var end = Fire(Ply(sh.p), d * v, r, mode, 3.0f / v, out var touched, noGravity: true);
                        csv.AppendLine(string.Join(",", "member", cfg, mode, r.ToString(ci), v.ToString(ci), i,
                            F(Ply(sh.p)), F(d), F(end), touched ? 1 : 0, sh.cap, 0, 0));
                    }
                for (int i = 0; i < shots.window.Length; i++)
                {
                    var sh = shots.window[i];
                    var d = Ply(sh.d).normalized;
                    const float v = 20f;
                    var end = Fire(Ply(sh.p), d * v, r, mode, 3.0f / v, out var touched, noGravity: true);
                    csv.AppendLine(string.Join(",", "window", cfg, mode, r.ToString(ci), v.ToString(ci), i,
                        F(Ply(sh.p)), F(d), F(end), touched ? 1 : 0, -1,
                        sh.s.ToString(ci), sh.y.ToString(ci)));
                }
            }
            roots[cfg].SetActive(false);
        }

        Physics.simulationMode = wasMode; Physics.gravity = wasGravity;
        File.WriteAllText(outPath, csv.ToString());
        Debug.Log("[VDGS] wrote " + outPath);
        if (Application.isBatchMode) EditorApplication.Exit(0);
    }

    /// <summary>One ball, one shot. Returns where it is after <paramref name="seconds"/>;
    /// <paramref name="touched"/> is whether it hit anything on the way.</summary>
    private static Vector3 Fire(Vector3 start, Vector3 vel, float r, CollisionDetectionMode mode,
                                float seconds, out bool touched, bool noGravity = false)
    {
        var ball = new GameObject("ball");
        ball.transform.position = start;
        var sc = ball.AddComponent<SphereCollider>();
        sc.radius = r;
        var rb = ball.AddComponent<Rigidbody>();
        rb.useGravity = !noGravity;
        rb.collisionDetectionMode = mode;
        rb.velocity = vel;
        Physics.SyncTransforms();
        int n = Mathf.CeilToInt(seconds / kStep);
        for (int i = 0; i < n; i++) Physics.Simulate(kStep);
        var end = ball.transform.position;
        // Collision callbacks do not run in edit mode, so "touched" is read off the path: a
        // ball nothing hit ends where free flight puts it.
        var t = n * kStep;
        var free = start + vel * t + (noGravity ? Vector3.zero : 0.5f * kGravity * t * t);
        touched = (end - free).magnitude > 0.01f;
        UnityEngine.Object.DestroyImmediate(ball);
        return end;
    }

    private static string F(Vector3 v)
    {
        var ci = CultureInfo.InvariantCulture;
        return v.x.ToString("0.0000", ci) + "," + v.y.ToString("0.0000", ci) + "," + v.z.ToString("0.0000", ci);
    }

    private static string Arg(string name)
    {
        var argv = Environment.GetCommandLineArgs();
        for (int i = 0; i < argv.Length - 1; i++)
            if (argv[i] == name) return argv[i + 1];
        return null;
    }
}
