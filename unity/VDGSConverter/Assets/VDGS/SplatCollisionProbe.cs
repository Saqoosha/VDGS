using System.Text;
using UnityEngine;

/// <summary>
/// Exercises the runtime collision loader in the editor, on a Mac, without VelociDrone.
///
/// The plugin's SplatCollision.cs is symlinked into this project rather than copied, so
/// pressing Play here runs the SAME code the game will run: same file format, same Mesh
/// construction, same off-thread bake, same MeshCollider settings. A separate test
/// implementation would prove nothing about the one that ships.
///
/// Deploying to the Windows box and flying costs a plugin build, a transfer and someone's
/// physical screen. This costs a keypress.
/// </summary>
public class SplatCollisionProbe : MonoBehaviour
{
    /// <summary>
    /// Public door onto the plugin's internal loader.
    ///
    /// SplatCollision is `internal`, which is right for the plugin, and Unity puts editor
    /// scripts in a different assembly - so CollisionTest cannot call it directly. Rather
    /// than widen the plugin's surface for the sake of a test, the runtime assembly
    /// forwards.
    /// </summary>
    public static bool Attach(Transform parent, string splatDir, StringBuilder log)
    {
        return VDGS.SplatCollision.Attach(parent, splatDir, log);
    }

    /// <summary>Name of the child the loader creates, so callers can find the collider.</summary>
    public static string ChildName(string parentName)
    {
        return VDGS.SplatCollision.CollisionChildName(parentName);
    }

    /// <summary>
    /// Where the loader will look, so a test resolves the path the same way the game does.
    ///
    /// The two layouts are not interchangeable: a converted scene keeps its mesh inside its
    /// directory, a .ply scene keeps it beside the file as &lt;name&gt;.collision.bin. A test
    /// that only knew the first would report "no collision.bin" for every .ply capture.
    /// </summary>
    public static string PathFor(string dirOrPly)
    {
        return VDGS.SplatCollision.PathFor(dirOrPly);
    }

    /// <summary>
    /// One drop point, for the in-editor probe. See DropPoints for how it is chosen.
    /// </summary>
    public static Vector3 InteriorDropPoint(MeshCollider col, float height, out string how)
    {
        var points = DropPoints(col, 0.125f, height, 1, out var hows);
        if (points.Count > 0)
        {
            how = hows[0];
            return points[0];
        }
        how = "no supporting surface found anywhere on the shell - falling back to its centre";
        return col.bounds.center;
    }

    /// <summary>
    /// Up to <paramref name="wanted"/> places a ball of <paramref name="radius"/> could be
    /// dropped and land on something.
    ///
    /// Two lessons are baked in, both from runs that failed for the wrong reason:
    ///
    ///  - The middle of the AABB is not necessarily over anything. It works for a room and
    ///    fails for a site: textilni spans 64 x 80 m and its box centre is open air.
    ///  - A zero-width Raycast finds surfaces a ball cannot rest on. Aiming at a vertex of
    ///    a sub-centimetre sliver produced a hit at y=-1.923 that the ball slid straight
    ///    off, and the run read as "fell through" - the mesh was fine. SphereCast with the
    ///    ball's own radius only reports surfaces that can actually hold it.
    ///
    /// Several points rather than one because one spot says almost nothing about a large
    /// capture. The rate still does NOT say whether the shell is aligned - see CollisionTest,
    /// which scores 3 of 8 on a capture confirmed correct in the game.
    /// </summary>
    public static System.Collections.Generic.List<Vector3> DropPoints(
        MeshCollider col, float radius, float height, int wanted, out string[] hows)
    {
        var b = col.bounds;
        var points = new System.Collections.Generic.List<Vector3>();
        var notes = new System.Collections.Generic.List<string>();

        foreach (var xz in Columns(col, b, wanted * 4))
        {
            if (points.Count >= wanted) break;

            float support;
            var from = LowestOpenPocket(col, xz, b, radius, out support);
            if (float.IsNaN(from)) continue;

            notes.Add($"({xz.x:0.00}, {xz.y:0.00}): open down to y={from:0.000}, "
                      + $"surface below at y={support:0.000}");
            points.Add(new Vector3(xz.x, support + radius + height, xz.y));
        }

        hows = notes.ToArray();
        return points;
    }

    /// <summary>
    /// The lowest height in this column where the ball fits AND something is under it.
    ///
    /// This replaced two earlier rules that both aimed the test at the wrong thing:
    ///
    ///  - "the topmost surface" lands the ball on the ROOF, outside the capture.
    ///  - "the lowest surface a cast finds" lands it inside furniture or under the floor
    ///    slab, so it starts embedded in material and is pushed out through the bottom.
    ///
    /// A drone flies in open space just above a floor. That is what this looks for, and it
    /// needs no idea of which surface is "the" floor: walk down while a sphere of the ball's
    /// size still fits, and keep the lowest position that has a surface beneath it.
    ///
    /// Returns NaN when the column has no such place.
    /// </summary>
    private static float LowestOpenPocket(
        MeshCollider col, Vector2 xz, Bounds b, float radius, out float support)
    {
        var best = float.NaN;
        support = float.NaN;

        var floorLimit = b.min.y - 1f;
        var step = radius * 0.5f;
        var steps = Mathf.Min(2000, Mathf.CeilToInt((b.size.y + 2f) / step));

        for (int i = 0; i < steps; i++)
        {
            var y = b.max.y + 1f - i * step;
            if (y <= floorLimit) break;

            var p = new Vector3(xz.x, y, xz.y);
            if (Physics.CheckSphere(p, radius)) continue;       // inside something

            RaycastHit hit;
            if (!Physics.SphereCast(p, radius, Vector3.down, out hit, y - floorLimit)) continue;
            if (hit.collider != col) continue;

            best = y;
            support = hit.point.y;
        }
        return best;
    }

    /// <summary>XZ positions worth casting down from: the box centre, then the mesh itself.</summary>
    private static System.Collections.Generic.List<Vector2> Columns(
        MeshCollider col, Bounds b, int wanted)
    {
        var list = new System.Collections.Generic.List<Vector2> { new Vector2(b.center.x, b.center.z) };

        var mesh = col.sharedMesh;
        if (mesh == null) return list;
        var verts = mesh.vertices;
        if (verts.Length == 0) return list;

        // Strided rather than random, so the same capture always yields the same columns and
        // a failure can be re-run; strided rather than sequential, so the picks spread over
        // the whole surface instead of clustering on whatever the mesh happens to start with.
        var step = Mathf.Max(1, verts.Length / Mathf.Max(1, wanted));
        for (int i = 0; i < verts.Length && list.Count <= wanted; i += step)
        {
            var w = col.transform.TransformPoint(verts[i]);
            list.Add(new Vector2(w.x, w.z));
        }
        return list;
    }


    [Tooltip("Directory holding collision.bin - normally build/splats/<scene>/")]
    public string splatDir = "";

    [Tooltip("Draw the collision mesh. A MeshCollider renders nothing on its own, so " +
             "without this the shell is invisible and only the falling ball shows it exists")]
    public bool showMesh = true;

    [Tooltip("off / solid / wire - solid culls back faces, which is the orientation test")]
    public string viewMode = "solid";

    [Tooltip("Drop a ball inside the capture to prove the collider actually stops it")]
    public bool dropTestBall = true;

    [Tooltip("How far above the surface it will land on the ball starts")]
    public float dropHeight = 2f;

    private GameObject m_Ball;
    private float m_StartY;
    private float m_Elapsed;
    private bool m_Reported;

    private void Start()
    {
        var log = new StringBuilder();
        log.AppendLine("[VDGS] collision probe: " + splatDir);

        var ok = VDGS.SplatCollision.Attach(transform, splatDir, log);
        Debug.Log(log.ToString().TrimEnd());

        if (!ok)
        {
            Debug.LogWarning("[VDGS] no collider attached - nothing to test");
            enabled = false;
            return;
        }

        if (showMesh) StartCoroutine(ShowWhenBaked());
        if (dropTestBall) StartCoroutine(DropWhenBaked());
    }

    /// <summary>
    /// Draw the shell with the plugin's own view code, so this exercises what ships.
    ///
    /// An earlier version here built its own Unlit/Color renderer. It proved nothing about
    /// the game - and it was quietly opaque, because Unlit/Color ignores the alpha channel.
    /// SplatCollisionView is the same class the mod uses in VelociDrone.
    /// </summary>
    private System.Collections.IEnumerator ShowWhenBaked()
    {
        var child = transform.Find(ChildName(name));
        var col = child != null ? child.GetComponent<MeshCollider>() : null;
        if (col == null) yield break;

        var waited = 0f;
        while (col.sharedMesh == null && waited < 30f)
        {
            waited += Time.deltaTime;
            yield return null;
        }
        if (col.sharedMesh == null) yield break;

        var log = new StringBuilder();
        VDGS.SplatCollisionView.SetMode(transform, viewMode, log);
        Debug.Log("[VDGS] " + log.ToString().TrimEnd()
                  + "\n[VDGS] press V for solid, W for wire, C to hide");
    }

    private void CycleView(string mode)
    {
        var log = new StringBuilder();
        VDGS.SplatCollisionView.SetMode(transform, mode, log);
        if (log.Length > 0) Debug.Log("[VDGS] " + log.ToString().TrimEnd());
    }

    /// <summary>
    /// Wait for the collider to have its mesh before dropping anything.
    ///
    /// The bake runs on a worker thread, so sharedMesh is null for the first frames. A ball
    /// dropped before then falls straight through and the run looks like a failure of the
    /// collider rather than of the test.
    /// </summary>
    private System.Collections.IEnumerator DropWhenBaked()
    {
        var child = transform.Find(ChildName(name));
        var col = child != null ? child.GetComponent<MeshCollider>() : null;
        if (col == null) yield break;

        var waited = 0f;
        while (col.sharedMesh == null && waited < 30f)
        {
            waited += Time.deltaTime;
            yield return null;
        }
        if (col.sharedMesh == null)
        {
            Debug.LogError("[VDGS] collider never got its mesh after 30 s");
            yield break;
        }
        Debug.Log($"[VDGS] collider ready after {waited:0.00} s, " +
                  $"bounds {col.bounds.min} .. {col.bounds.max}");

        string how;
        var from = InteriorDropPoint(col, dropHeight, out how);
        Debug.Log("[VDGS] drop point: " + how);

        m_Ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        m_Ball.name = "collision probe ball";
        m_Ball.transform.localScale = Vector3.one * 0.25f;   // about a 5 inch quad
        m_Ball.transform.position = from;
        var rb = m_Ball.AddComponent<Rigidbody>();
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        m_StartY = from.y;
        Debug.Log($"[VDGS] dropping a ball from {from}");
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.C)) CycleView(VDGS.SplatCollisionView.kOff);
        if (Input.GetKeyDown(KeyCode.V)) CycleView(VDGS.SplatCollisionView.kSolid);
        if (Input.GetKeyDown(KeyCode.W)) CycleView(VDGS.SplatCollisionView.kWire);

        if (m_Ball == null || m_Reported) return;
        m_Elapsed += Time.deltaTime;

        var rb = m_Ball.GetComponent<Rigidbody>();
        if (rb != null && rb.IsSleeping())
        {
            var y = m_Ball.transform.position.y;
            Debug.Log($"[VDGS] ball came to rest at y={y:0.000} after {m_Elapsed:0.00} s, " +
                      $"having fallen {m_StartY - y:0.000}  -> COLLIDER WORKS");
            m_Reported = true;
            return;
        }

        // A ball that keeps going well past the collider went through it.
        var child = transform.Find(ChildName(name));
        var col = child != null ? child.GetComponent<MeshCollider>() : null;
        if (col != null && m_Ball.transform.position.y < col.bounds.min.y - 5f)
        {
            Debug.LogError($"[VDGS] ball fell through: y={m_Ball.transform.position.y:0.00} " +
                           $"is below the collider's floor at {col.bounds.min.y:0.00}");
            m_Reported = true;
        }
    }
}
