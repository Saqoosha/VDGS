using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Loads a collision.bin the way the game will, then drops a ball on it - headless.
///
///   Unity -batchmode -quit -nographics -projectPath unity/VDGSConverter \
///         -executeMethod CollisionTest.Run -vdgsCollision build/splats/playroom
///
/// This runs the plugin's own SplatCollision.cs, symlinked into this project rather than
/// copied, so it exercises the code that ships: same file format, same Mesh construction,
/// same MeshCollider settings. A separate test implementation would prove nothing.
///
/// Headless rather than Play mode on purpose. Physics.Simulate steps the world by hand, so
/// it is a command - repeatable, diffable, runnable before anything reaches the Windows box.
///
/// A DIAGNOSTIC, NOT A GATE. It fails only when nothing stops anything; the on-surface rate
/// it prints is a number to read, not a threshold to pass. Whether a shell sits where the
/// walls look like they are cannot be decided from the mesh - the void under a floor is
/// open space with a surface below it exactly as the room above is - and drjohnson, which
/// is confirmed correct in the game, scores 3 of 8 here. Alignment is judged by flying.
/// </summary>
public static class CollisionTest
{
    /// <summary>How many places to drop a ball. Enough to be a rate, cheap enough to run
    /// on every mesh: 8 drops of up to 10 simulated seconds each.</summary>
    private const int kColumns = 8;

    /// <summary>How far above the surface each ball starts. Short on purpose: a long fall
    /// lets the ball roll off before it settles, and rolling reads as a failure.</summary>
    private const float kDropHeight = 0.3f;

    /// <summary>How far from the aimed-at surface still counts as landing on it.</summary>
    private const float kTolerance = 0.15f;

    public static void Run()
    {
        var dir = Arg("-vdgsCollision");
        if (string.IsNullOrEmpty(dir))
        {
            Fail("pass -vdgsCollision <dir containing collision.bin>");
            return;
        }

        // Resolved by the loader, not by this test: pass a directory for a converted scene
        // or the .ply itself for a runtime-loaded one, and both find their mesh.
        var path = SplatCollisionProbe.PathFor(dir);
        if (!File.Exists(path))
        {
            Fail("no collision mesh at " + path);
            return;
        }

        var log = new StringBuilder();
        var root = new GameObject("VDGS_test");
        if (!SplatCollisionProbe.Attach(root.transform, dir, log))
        {
            Debug.Log(log.ToString().TrimEnd());
            Fail("Attach returned false");
            return;
        }
        Debug.Log("[VDGS] " + log.ToString().TrimEnd());

        var child = root.transform.Find(SplatCollisionProbe.ChildName(root.name));
        var col = child != null ? child.GetComponent<MeshCollider>() : null;
        if (col == null || col.sharedMesh == null)
        {
            Fail("collider has no mesh");
            return;
        }

        var b = col.bounds;
        Debug.Log($"[VDGS] collider bounds {b.min} .. {b.max}");
        Debug.Log($"[VDGS] convex={col.convex} cooking={col.cookingOptions} "
                  + $"layer={child.gameObject.layer} isTrigger={col.isTrigger} "
                  + $"material={(col.sharedMaterial == null ? "none" : col.sharedMaterial.name)}");

        // Several drop points, not one. A single spot says almost nothing about a 64 x 80 m
        // capture, and the one it happens to pick can be a sliver: aiming at a vertex of a
        // sub-centimetre feature in textilni produced a hit the ball slid off, and the run
        // read as "fell through" while the mesh was fine.
        const float radius = 0.125f;                        // about a 5 inch quad
        var from = SplatCollisionProbe.DropPoints(col, radius, kDropHeight, kColumns, out var hows);
        if (from.Count == 0)
        {
            Fail("no surface anywhere on this mesh can hold a "
                 + (radius * 2f).ToString("0.00") + " m ball");
            return;
        }
        for (int i = 0; i < hows.Length; i++) Debug.Log("[VDGS] drop " + (i + 1) + " " + hows[i]);

        // The game runs at 400 Hz (Fixed Timestep 0.0025 in globalgamemanagers), so step at
        // the same rate - a coarser step is exactly what lets a fast body tunnel, and
        // testing at a rate the game does not use would measure the wrong physics.
        //
        // Gravity here is Unity's -9.81, not VelociDrone's -10.78. It does not matter for
        // "does the collider stop a falling body", but anything measuring tunnelling speed
        // has to set it to match first.
        const float step = 0.0025f;
        var wasMode = Physics.simulationMode;
        Physics.simulationMode = SimulationMode.Script;

        // "Something held it" is not the question, and asking that cannot tell a correct
        // mesh from an inside-out one: playroom scores 6 of 8 either way, because a shell
        // has surfaces facing both directions at different depths and a ball that sinks
        // through the floor still lands on the floor's underside. The question is whether
        // the ball stops ON THE SURFACE A DOWNWARD CAST FOUND - the one a pilot sees. A
        // mesh wound the wrong way lets it pass and catches it somewhere else, which is
        // exactly the "drone sinks below the floor" complaint.
        var onSurface = 0;
        var elsewhere = 0;
        var through = 0;
        for (int n = 0; n < from.Count; n++)
        {
            // DropPoints returns surface + radius + kDropHeight, and a resting sphere's
            // centre sits a radius above the surface.
            var expected = from[n].y - kDropHeight;
            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.transform.localScale = Vector3.one * (radius * 2f);
            ball.transform.position = from[n];
            var rb = ball.AddComponent<Rigidbody>();
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var settled = -1f;
            var t = 0f;
            for (int i = 0; i < 4000 && settled < 0f; i++)     // 10 seconds
            {
                Physics.Simulate(step);
                t += step;
                if (rb.IsSleeping() || (rb.velocity.magnitude < 0.01f && t > 0.5f)) settled = t;
                if (ball.transform.position.y < b.min.y - 5f) break;
            }

            var rest = ball.transform.position;
            var y = rest.y;
            var off = y - expected;
            // Full position, not just height: whether a ball SANK through a surface or
            // ROLLED off one cannot be told from y alone, and the parity check that settles
            // it needs the xz it ended up at.
            Debug.Log($"[VDGS]   rest{n + 1} {rest.x:0.000} {rest.y:0.000} {rest.z:0.000}");
            if (settled < 0f && y < b.min.y - 5f)
            {
                through++;
                Debug.Log($"[VDGS]   {n + 1}: THROUGH  expected y={expected:0.000}, "
                          + $"left the mesh entirely");
            }
            else if (settled >= 0f && Mathf.Abs(off) <= kTolerance)
            {
                onSurface++;
                Debug.Log($"[VDGS]   {n + 1}: on surface  y={y:0.000} "
                          + $"(expected {expected:0.000}, off {off:+0.000})");
            }
            else
            {
                elsewhere++;
                Debug.Log($"[VDGS]   {n + 1}: ELSEWHERE  y={y:0.000} "
                          + $"(expected {expected:0.000}, off {off:+0.000})"
                          + (settled < 0f ? " and still moving at 10 s" : ""));
            }
            UnityEngine.Object.DestroyImmediate(ball);
        }
        Physics.simulationMode = wasMode;

        Debug.Log($"[VDGS] on surface {onSurface}, elsewhere {elsewhere}, through {through}"
                  + $"  of {from.Count} drops");

        // A rate, not a verdict. "Which side is flyable" is not decidable from the mesh -
        // the void under a floor is open space with a surface below it just as much as the
        // room above is - so a drop aimed by geometry alone lands in the wrong place often
        // enough that no threshold separates a good mesh from a bad one. Measured: playroom
        // and drjohnson both score 2-3 of 8 here, and drjohnson is confirmed correct in the
        // game. Gating on this number would have blocked a working collider.
        //
        // So the only failure is the unambiguous one: nothing stopped anything.
        if (through == from.Count)
        {
            Fail("every drop left the mesh - it stops nothing anywhere");
            return;
        }

        Debug.Log("[VDGS] collision loaded and stops a falling body. The on-surface rate "
                  + "above is diagnostic - judge alignment by flying, not by this number.");
        if (Application.isBatchMode) EditorApplication.Exit(0);
    }

    private static string Arg(string name)
    {
        var argv = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < argv.Length - 1; i++)
            if (argv[i] == name) return argv[i + 1];
        return null;
    }

    private static void Fail(string why)
    {
        Debug.LogError("[VDGS] collision test FAILED: " + why);
        if (Application.isBatchMode) EditorApplication.Exit(1);
    }
}
