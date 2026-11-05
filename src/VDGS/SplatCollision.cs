using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// The collision surface for a splat scene: a MeshCollider built at runtime from
    /// collision.bin, plus the capsules and boxes of parts.json.
    ///
    /// A capture is a cloud of oriented blobs with no triangles, so the drone flies
    /// through walls and falls through floors. The shape is generated offline (OpenVDB
    /// level set - see docs/superpowers/specs/2026-08-18-splat-collision-design.md) and
    /// arrives as vertices and triangle indices, nothing else. Everything the collision
    /// then does - deformation, impact sound, crash detection - is the game's own code
    /// reacting to a collider being there.
    ///
    /// parts.json is the second shape, for the things a density-field shell does badly.
    /// A 7 cm gate tube came out 45 cm wide from the level set, because the inflation
    /// that keeps thin walls closed fattens every thin member by the same amount. So gates
    /// and flags are fitted offline as primitives (tools/fit_parts.py): a CapsuleCollider
    /// per tube, a BoxCollider per banner-type panel. PhysX treats both as solid shapes
    /// with no triangles, so there is nothing to cook and nothing to tunnel through from
    /// behind. The mesh stays for the ground. Either file alone is a collision.
    ///
    /// Settings copy what VelociDrone already ships: Office has 598 colliders, all on
    /// layer Default, and its MeshColliders are non-convex with cookingOptions 30 and no
    /// physics material. The drone's own layer, QuadColliders(13), collides with Default.
    ///
    /// This component lives on a CHILD of the splat root, which is what makes
    /// placement.json work: position, rotation and scale come from the parent for free, so
    /// nothing here holds a coordinate. The in-game scale and height controls move the
    /// collider with the capture without knowing it exists.
    /// </summary>
    internal class SplatCollision : MonoBehaviour
    {
        private const uint kVersion = 1;

        /// <summary>Matches the MeshColliders VelociDrone ships in Office.</summary>
        private const MeshColliderCookingOptions kCooking =
            MeshColliderCookingOptions.CookForFasterSimulation |
            MeshColliderCookingOptions.EnableMeshCleaning |
            MeshColliderCookingOptions.WeldColocatedVertices |
            MeshColliderCookingOptions.UseFastMidphase;

        private Mesh m_Mesh;
        private MeshCollider m_Collider;

        /// <summary>
        /// The primitives, in this child's local frame (already mirrored when the mesh
        /// was). Kept after the colliders are made because the view draws them from here:
        /// a CapsuleCollider renders nothing, same as a MeshCollider.
        /// </summary>
        private readonly List<PartCapsule> m_Capsules = new List<PartCapsule>();
        private readonly List<PartBox> m_Boxes = new List<PartBox>();

        /// <summary>What the view draws: the baked mesh and the primitives as one Mesh.
        /// Built on demand, kept until destroyed.</summary>
        private Mesh m_DisplayMesh;

        // What parts.json holds. A box carries three axis vectors rather than one 3x3.
        [Serializable] private class PartCapsule { public float[] a; public float[] b; public float r; }
        [Serializable] private class PartBox { public float[] c; public float[] u; public float[] v; public float[] w; public float[] size; }
        [Serializable] private class PartsFile { public int version; public string frame; public PartCapsule[] capsules; public PartBox[] boxes; }

        /// <summary>
        /// True while the worker thread is inside Physics.BakeMesh.
        ///
        /// `volatile` because two threads read and write it with no lock between them. It
        /// works without on Mono/x64, which is exactly why it would never be noticed if the
        /// platform changed.
        /// </summary>
        private volatile bool m_Baking;

        /// <summary>The bake thread, kept so OnDestroy can wait for it.</summary>
        private Thread m_Worker;

        internal static string PathFor(string dir)
        {
            // A single-file capture (.ply or a bundled .sog) keeps its mesh beside it; a
            // directory keeps it inside.
            return dir.EndsWith(".ply", StringComparison.OrdinalIgnoreCase)
                || dir.EndsWith(".sog", StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(dir, ".collision.bin")
                : Path.Combine(dir, "collision.bin");
        }

        /// <summary>Same layout rule as PathFor, for the primitives file.</summary>
        internal static string PartsPathFor(string dir)
        {
            return dir.EndsWith(".ply", StringComparison.OrdinalIgnoreCase)
                || dir.EndsWith(".sog", StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(dir, ".parts.json")
                : Path.Combine(dir, "parts.json");
        }

        /// <summary>
        /// Adds the collider under <paramref name="parent"/>, or reports why not.
        ///
        /// Returns false when there is no collision.bin, which is the normal case for a
        /// capture nobody has generated one for - not an error.
        ///
        /// <paramref name="mirrorY"/> is decided by the caller, not here. SplatScene reads
        /// it from placement.json (the same value it hands to PlyLoader) and passes it in,
        /// so the splats and the collision shell are always mirrored together - two
        /// independently-decided flags that happen to agree today would silently stop
        /// agreeing the moment either becomes settable. Left null, the mesh mirrors by the
        /// old per-extension rule; the only caller that relies on that is the Mac-only
        /// SplatCollisionProbe editor tool, which tests the collider directly and has no
        /// placement.json to read.
        /// </summary>
        internal static bool Attach(Transform parent, string dir, StringBuilder log, bool? mirrorY = null)
        {
            var path = PathFor(dir);
            var partsPath = PartsPathFor(dir);
            var haveMesh = File.Exists(path);
            var haveParts = File.Exists(partsPath);
            if (!haveMesh && !haveParts)
            {
                log.AppendLine("  no collision.bin or parts.json at " + Path.GetDirectoryName(path));
                return false;
            }

            if (parent.Find(CollisionChildName(parent.name)) != null)
            {
                log.AppendLine("  collision already attached");
                return true;
            }

            var go = new GameObject(CollisionChildName(parent.name));
            go.transform.SetParent(parent, false);
            go.layer = 0;   // Default, which is what QuadColliders collides with

            var self = go.AddComponent<SplatCollision>();

            // A .ply scene is mirrored in Y by PlyLoader as it is read, because a capture is
            // right-handed Y-down and Unity is left-handed Y-up. The collision mesh is built
            // from the ply BEFORE that happens, so it has to take the same reflection or it
            // sits upside down under a capture that isn't. A converted directory was already
            // mirrored by reprocess.sh before export, so it must NOT be touched here.
            //
            // The rule stays here rather than in the offline tool because it is a property
            // of how the runtime reads a .ply, and belongs next to the code that does it -
            // put it in the pipeline instead and the two drift apart, which is what happened
            // once already. "As this shape has always behaved" is only the fallback for a
            // caller that does not know better - see the mirrorY doc comment above.
            var mirror = mirrorY ?? dir.EndsWith(".ply", StringComparison.OrdinalIgnoreCase);
            // Each file loads on its own: a bad parts.json does not take the ground with
            // it, and a capture with only primitives (no shell at all) is still solid.
            var ok = false;
            if (haveMesh) ok |= self.Load(path, mirror, log);
            if (haveParts) ok |= self.LoadParts(partsPath, mirror, log);
            if (ok) return true;

            // Leave nothing behind. The child alone is what the "already attached" guard
            // above looks for, so an orphan from a failed load makes every later Attach
            // answer true with no collider present - while IsAttached answers false. The
            // two then disagree about the same object, and the load can never be retried.
            // Renamed first: Destroy is deferred to the end of the frame, so a second Attach
            // in the same frame would otherwise find this doomed child by name and report
            // "already attached" with no collider on it. Off the name, it cannot be found.
            //
            // DestroyImmediate in edit mode - Destroy there logs a complaint and does
            // nothing, so the orphan this exists to prevent would survive in exactly the
            // environment CollisionTest uses to check a mesh before it ships.
            go.name = CollisionChildName(parent.name) + "_failed";
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
            return false;
        }

        /// <summary>
        /// The child's name IS the handle - nothing holds a reference to it - so every
        /// lookup, here and in SplatCollisionView, goes through this one method. Spelled
        /// out a second time elsewhere, a rename would silently stop that file finding the
        /// collider, and the symptom would be "no collider to draw" rather than an error.
        /// </summary>
        internal static string CollisionChildName(string parentName)
        {
            return parentName + "_collision";
        }

        internal static bool IsAttached(Transform parent)
        {
            var child = parent.Find(CollisionChildName(parent.name));
            return child != null && child.GetComponentsInChildren<Collider>(true).Length > 0;
        }

        /// <summary>True when a collision mesh or a parts file has been generated for this capture.</summary>
        internal static bool Exists(string dir)
        {
            return File.Exists(PathFor(dir)) || File.Exists(PartsPathFor(dir));
        }

        /// <summary>
        /// Turn the collider on or off without rebuilding it.
        ///
        /// Toggling `enabled` rather than attaching and detaching is the whole point of
        /// keeping the collider attached from spawn: cooking drjohnson's 278K triangles
        /// costs a stutter, and the reason to have a switch at all is to flip it WHILE
        /// flying, comparing a wall that stops you against one that does not.
        /// </summary>
        internal static bool SetEnabled(Transform parent, bool on)
        {
            var child = parent.Find(CollisionChildName(parent.name));
            if (child == null) return false;
            var all = child.GetComponentsInChildren<Collider>(true);
            if (all.Length == 0) return false;
            foreach (var c in all) c.enabled = on;
            return true;
        }

        /// <summary>
        /// True while the collider exists but its mesh is still being cooked.
        ///
        /// This is the only reason drawing the shell can fail and then succeed on a retry.
        /// Callers that queue work for "when it is ready" should ask this rather than infer
        /// it from the collider existing - a missing shader also fails, and never recovers.
        /// </summary>
        internal static bool IsBaking(Transform parent)
        {
            var col = ColliderOn(parent);
            return col != null && col.sharedMesh == null;
        }

        /// <summary>False when there is no collider at all, so callers cannot mistake
        /// "no mesh" for "switched off".</summary>
        internal static bool IsEnabled(Transform parent)
        {
            var child = parent.Find(CollisionChildName(parent.name));
            if (child == null) return false;
            foreach (var c in child.GetComponentsInChildren<Collider>(true))
                if (c.enabled) return true;
            return false;
        }

        private static MeshCollider ColliderOn(Transform parent)
        {
            var child = parent.Find(CollisionChildName(parent.name));
            return child != null ? child.GetComponent<MeshCollider>() : null;
        }

        /// <summary>
        /// Everything that is solid here, as one Mesh for drawing: the baked shell and the
        /// primitives. Null while the shell is still cooking (the only case worth a retry),
        /// and when there is nothing at all. The combined mesh is built once and kept.
        /// </summary>
        internal static Mesh DisplayMeshOn(Transform parent, StringBuilder log)
        {
            var child = parent.Find(CollisionChildName(parent.name));
            var self = child != null ? child.GetComponent<SplatCollision>() : null;
            if (self == null) return null;
            if (self.m_Collider != null && self.m_Collider.sharedMesh == null) return null;
            if (self.m_DisplayMesh != null) return self.m_DisplayMesh;

            var hasParts = self.m_Capsules.Count + self.m_Boxes.Count > 0;
            if (!hasParts) return self.m_Mesh;
            var parts = self.BuildPartsMesh();
            if (self.m_Mesh == null)
            {
                self.m_DisplayMesh = parts;
                return parts;
            }
            var combined = new Mesh { name = self.gameObject.name + "_display" };
            combined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            combined.CombineMeshes(new[]
            {
                new CombineInstance { mesh = self.m_Mesh, transform = Matrix4x4.identity },
                new CombineInstance { mesh = parts, transform = Matrix4x4.identity },
            }, true, false);
            if (Application.isPlaying) UnityEngine.Object.Destroy(parts); else UnityEngine.Object.DestroyImmediate(parts);
            self.m_DisplayMesh = combined;
            log?.AppendLine($"  display mesh: shell + {self.m_Capsules.Count} capsules + {self.m_Boxes.Count} boxes");
            return combined;
        }

        private bool Load(string path, bool mirrorY, StringBuilder log)
        {
            Vector3[] verts;
            int[] tris;
            try
            {
                Read(path, out verts, out tris);
            }
            catch (Exception e)
            {
                log.AppendLine("  collision load failed: " + e.Message);
                return false;
            }

            try
            {
                return Build(verts, tris, mirrorY, log);
            }
            catch (Exception e)
            {
                // Mesh construction is inside the guard too, not just the file read. An
                // exception here - realistically running out of memory on a large mesh -
                // used to escape Attach before it could rename and destroy the child, so
                // the child stayed under the real name with no MeshCollider and every later
                // Attach reported "already attached" while IsAttached said otherwise.
                log.AppendLine("  collision build failed: " + e.Message);
                return false;
            }
        }

        private bool Build(Vector3[] verts, int[] tris, bool mirrorY, StringBuilder log)
        {

            if (mirrorY)
            {
                for (int i = 0; i < verts.Length; i++)
                    verts[i].y = -verts[i].y;

                // A reflection has determinant -1, so every triangle now faces the other way.
                // Swapping two indices puts the solid back on the side it was on; without it
                // the shell is inside-out, and PhysX single-sided triangles mean a drone
                // passes through the floor instead of landing on it.
                for (int i = 0; i < tris.Length; i += 3)
                {
                    var t = tris[i + 1];
                    tris[i + 1] = tris[i + 2];
                    tris[i + 2] = t;
                }
                log.AppendLine("  mirrored in Y to match PlyLoader, winding reversed with it");
            }

            m_Mesh = new Mesh { name = gameObject.name };
            // Not optional. A collision mesh runs to hundreds of thousands of vertices and
            // the default 16-bit index buffer silently wraps at 65535.
            m_Mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m_Mesh.SetVertices(verts);
            m_Mesh.SetTriangles(tris, 0, false);
            m_Mesh.RecalculateBounds();

            m_Collider = gameObject.AddComponent<MeshCollider>();
            m_Collider.convex = false;
            m_Collider.cookingOptions = kCooking;

            log.AppendLine($"  collision: {verts.Length:N0} verts, {tris.Length / 3:N0} tris"
                           + $"  bounds {m_Mesh.bounds.size}");

            // Coroutines need a running player loop. Outside one - an editor batch job
            // building a scene, or a headless physics check - there is nothing to drive
            // them, and a collider whose mesh is assigned by a coroutine that never runs
            // is silently inert. Cook inline there; the stutter it costs does not matter
            // when nobody is flying.
            if (Application.isPlaying)
                StartCoroutine(BakeThenAssign(log));
            else
                BakeInline(log);
            return true;
        }

        /// <summary>
        /// Cook the mesh on a worker thread, then hand the finished result to the collider.
        ///
        /// Assigning sharedMesh cooks synchronously on the main thread, and spawning a
        /// capture already stalls for hundreds of milliseconds uploading tens of megabytes
        /// to the GPU. Physics.BakeMesh does the same work off-thread and caches it against
        /// the mesh, so the assignment afterwards only picks up what is already baked.
        /// </summary>
        private IEnumerator BakeThenAssign(StringBuilder log)
        {
            var id = m_Mesh.GetInstanceID();
            Exception failure = null;

            m_Worker = new Thread(() =>
            {
                try { Physics.BakeMesh(id, false); }
                catch (Exception e) { failure = e; }
                finally { m_Baking = false; }
            });
            m_Worker.IsBackground = true;
            m_Baking = true;
            try
            {
                m_Worker.Start();
            }
            catch (Exception e)
            {
                // Out of threads. Clear the flag by hand - the finally above never ran - or
                // OnDestroy would refuse to free the mesh for the object's whole life.
                m_Baking = false;
                m_Worker = null;
                log.AppendLine("  could not start the bake thread: " + e.Message);
                BakeInline(log);
                yield break;
            }

            while (m_Baking) yield return null;

            if (failure != null)
            {
                // Worth continuing: the assignment below will cook on the main thread
                // instead, which costs a stutter but still produces a working collider.
                // Debug, not VdgsPlugin.Log, and that is a compromise rather than a preference.
                // The project's rule is to log through BepInEx, because BepInEx.cfg sets
                // UnityLogListening = false and Player.log is buried under PostProcessing
                // spam. But this file is symlinked into unity/VDGSConverter, which compiles
                // it WITHOUT BepInEx, so naming VdgsPlugin here breaks the editor test that
                // exists to exercise this exact code. Until there is a UnityEngine-only seam
                // onto the plugin log, this warning is hard to see in game.
                Debug.LogWarning("[VDGS] BakeMesh failed, cooking inline: " + failure.Message);
            }

            m_Collider.sharedMesh = m_Mesh;
        }

        /// <summary>
        /// parts.json -> one CapsuleCollider or BoxCollider per entry, each on its own
        /// child so it can carry its own rotation. Nothing to cook: these are PhysX
        /// primitives, solid from every side, which is the whole reason they exist.
        /// </summary>
        private bool LoadParts(string path, bool mirrorY, StringBuilder log)
        {
            PartsFile parts;
            try
            {
                var json = File.ReadAllText(path);
                // Not JsonUtility: in the game it reads "version" and silently leaves the
                // nested arrays null, while the same call works in the editor.
                parts = Newtonsoft.Json.JsonConvert.DeserializeObject<PartsFile>(json);
                if (parts == null) throw new InvalidDataException("not a JSON object");
                if (parts.version != 1) throw new InvalidDataException($"parts.json version {parts.version}, expected 1");
            }
            catch (Exception e)
            {
                log.AppendLine("  parts load failed: " + e.Message);
                return false;
            }

            int nc = 0, nb = 0, bad = 0;
            foreach (var c in parts.capsules ?? new PartCapsule[0])
            {
                if (c == null || !Finite3(c.a) || !Finite3(c.b) || !(c.r > 0f) || c.r > kMaxPartExtent)
                {
                    bad++; continue;
                }
                var a = Local(c.a, mirrorY); var b = Local(c.b, mirrorY);
                var axis = b - a;
                if (axis.sqrMagnitude < 1e-8f)
                {
                    bad++; continue;
                }
                var go = new GameObject("capsule");
                go.transform.SetParent(transform, false);
                go.layer = 0;
                go.transform.localPosition = (a + b) * 0.5f;
                go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, axis.normalized);
                var cc = go.AddComponent<CapsuleCollider>();
                cc.direction = 1;                                   // local Y, the axis above
                cc.radius = c.r;
                cc.height = axis.magnitude + 2f * c.r;              // Unity's height spans the caps
                m_Capsules.Add(new PartCapsule { a = V(a), b = V(b), r = c.r });
                nc++;
            }
            foreach (var bx in parts.boxes ?? new PartBox[0])
            {
                if (bx == null || !Finite3(bx.c) || !Finite3(bx.u) || !Finite3(bx.v) || !Finite3(bx.size)
                    || !(bx.size[0] > 0f && bx.size[1] > 0f && bx.size[2] > 0f))
                {
                    bad++; continue;
                }
                var u = Local(bx.u, mirrorY); var v = Local(bx.v, mirrorY);
                // Mirroring flips the handedness of (u, v, w); the box is symmetric in w, so
                // w is rebuilt from u and v either way and the frame is always a rotation.
                var w = Vector3.Cross(u, v);
                if (Mathf.Abs(u.magnitude - 1f) > 0.02f || Mathf.Abs(v.magnitude - 1f) > 0.02f
                    || Mathf.Abs(Vector3.Dot(u, v)) > 0.02f)
                {
                    bad++; continue;
                }
                var go = new GameObject("box");
                go.transform.SetParent(transform, false);
                go.layer = 0;
                go.transform.localPosition = Local(bx.c, mirrorY);
                // LookRotation: local z = w, local y = v, local x = cross(v, w) = u.
                go.transform.localRotation = Quaternion.LookRotation(w, v);
                var bc = go.AddComponent<BoxCollider>();
                bc.size = new Vector3(bx.size[0], bx.size[1], bx.size[2]);   // along u, v, w
                m_Boxes.Add(new PartBox { c = V(go.transform.localPosition), u = V(u), v = V(v), w = V(w), size = bx.size });
                nb++;
            }
            log.AppendLine($"  parts: {nc} capsules, {nb} boxes" + (bad > 0 ? $", {bad} entries rejected" : "")
                           + (mirrorY ? "  (mirrored in Y with the mesh)" : ""));
            return nc + nb > 0;
        }

        // Metres from the capture's origin. Past this it is a broken file, not a course.
        private const float kMaxPartExtent = 1e5f;

        private static bool Finite3(float[] f)
        {
            if (f == null || f.Length != 3) return false;
            foreach (var x in f)
                if (!(Mathf.Abs(x) <= kMaxPartExtent)) return false;
            return true;
        }

        private static Vector3 Local(float[] f, bool mirrorY)
        {
            return new Vector3(f[0], mirrorY ? -f[1] : f[1], f[2]);
        }

        private static float[] V(Vector3 v) { return new[] { v.x, v.y, v.z }; }

        /// <summary>
        /// Triangles for the primitives, in this child's local frame, for the view only.
        /// Coarse on purpose - 12 segments, 3 rings per cap - a few hundred capsules is a
        /// few thousand triangles. Wound to face outward under Unity's convention (see
        /// AddTri), so `solid` mode culls their back faces like the shell's.
        /// </summary>
        private Mesh BuildPartsMesh()
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            const int seg = 12, rings = 3;
            foreach (var c in m_Capsules)
            {
                var a = new Vector3(c.a[0], c.a[1], c.a[2]); var b = new Vector3(c.b[0], c.b[1], c.b[2]);
                var u = (b - a).normalized;
                var t = Vector3.Cross(u, Mathf.Abs(u.z) < 0.9f ? Vector3.forward : Vector3.right).normalized;
                var s = Vector3.Cross(u, t);
                var rows = new List<(Vector3 centre, float radius)>();
                for (int k = rings; k >= 0; k--)
                {
                    var ph = Mathf.PI / 2f * k / rings;
                    rows.Add((a - u * c.r * Mathf.Sin(ph), c.r * Mathf.Cos(ph)));
                }
                for (int k = 0; k <= rings; k++)
                {
                    var ph = Mathf.PI / 2f * k / rings;
                    rows.Add((b + u * c.r * Mathf.Sin(ph), c.r * Mathf.Cos(ph)));
                }
                var baseIdx = verts.Count;
                foreach (var (centre, radius) in rows)
                    for (int j = 0; j < seg; j++)
                    {
                        var g = 2f * Mathf.PI * j / seg;
                        verts.Add(centre + radius * (Mathf.Cos(g) * t + Mathf.Sin(g) * s));
                    }
                var mid = (a + b) * 0.5f;
                for (int i = 0; i < rows.Count - 1; i++)
                    for (int j = 0; j < seg; j++)
                    {
                        int p0 = baseIdx + i * seg + j, p1 = baseIdx + i * seg + (j + 1) % seg;
                        int q0 = p0 + seg, q1 = p1 + seg;
                        AddTri(verts, tris, p0, q0, p1, mid); AddTri(verts, tris, p1, q0, q1, mid);
                    }
            }
            foreach (var bx in m_Boxes)
            {
                var c = new Vector3(bx.c[0], bx.c[1], bx.c[2]);
                var u = new Vector3(bx.u[0], bx.u[1], bx.u[2]); var v = new Vector3(bx.v[0], bx.v[1], bx.v[2]);
                var w = new Vector3(bx.w[0], bx.w[1], bx.w[2]);
                var h = new Vector3(bx.size[0], bx.size[1], bx.size[2]) * 0.5f;
                var baseIdx = verts.Count;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                            verts.Add(c + u * (sx * h.x) + v * (sy * h.y) + w * (sz * h.z));
                int[][] faces = { new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 }, new[] { 0, 4, 5, 1 },
                                  new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 } };
                foreach (var f in faces)
                {
                    AddTri(verts, tris, baseIdx + f[0], baseIdx + f[1], baseIdx + f[2], c);
                    AddTri(verts, tris, baseIdx + f[0], baseIdx + f[2], baseIdx + f[3], c);
                }
            }
            var mesh = new Mesh { name = gameObject.name + "_parts" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0, false);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// One triangle, wound so its front faces away from <paramref name="inside"/>.
        ///
        /// Unity's front face is the side from which the vertices run clockwise, and with
        /// its left-handed axes that is the side Cross(p1 - p0, p2 - p0) points to - the
        /// same vector RecalculateNormals would produce. The check against a point known to
        /// be inside the shape makes the generators above independent of their own vertex
        /// order; the drop test in CollisionTest reads the normals back to confirm it.
        /// </summary>
        private static void AddTri(List<Vector3> verts, List<int> tris, int i0, int i1, int i2, Vector3 inside)
        {
            var p0 = verts[i0]; var n = Vector3.Cross(verts[i1] - p0, verts[i2] - p0);
            if (Vector3.Dot(n, p0 - inside) < 0f) { var t = i1; i1 = i2; i2 = t; }
            tris.Add(i0); tris.Add(i1); tris.Add(i2);
        }

        /// <summary>Same result as the coroutine, all on this thread.</summary>
        private void BakeInline(StringBuilder log)
        {
            try { Physics.BakeMesh(m_Mesh.GetInstanceID(), false); }
            catch (Exception e) { log.AppendLine("  BakeMesh failed, cooking on assign: " + e.Message); }
            m_Collider.sharedMesh = m_Mesh;
        }

        private static void Read(string path, out Vector3[] verts, out int[] tris)
        {
            using (var f = File.OpenRead(path))
            using (var r = new BinaryReader(f))
            {
                var version = r.ReadUInt32();
                if (version != kVersion)
                    throw new InvalidDataException($"collision.bin version {version}, expected {kVersion}");

                var vertCount = checked((int)r.ReadUInt32());
                var indexCount = checked((int)r.ReadUInt32());

                // An empty mesh passes every other check here - 0 % 3 is 0, the expected
                // length works out to the 12-byte header, no index is out of range - and
                // then every layer above reports a collider that stops nothing. That is the
                // stale-chunk.bin failure again: the file is valid, so nothing complains.
                if (vertCount < 3 || indexCount < 3)
                    throw new InvalidDataException($"{vertCount} vertices and {indexCount} "
                                                   + "indices is not a mesh");
                if (indexCount % 3 != 0)
                    throw new InvalidDataException($"{indexCount} indices is not a whole number of triangles");

                var expected = 12L + (long)vertCount * 12 + (long)indexCount * 4;
                if (f.Length != expected)
                    throw new InvalidDataException($"expected {expected} bytes for {vertCount} verts "
                                                   + $"and {indexCount} indices, file is {f.Length}");

                verts = new Vector3[vertCount];
                for (int i = 0; i < vertCount; i++)
                {
                    var v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

                    // ReadSingle happily returns NaN for a corrupt word. One NaN vertex makes
                    // RecalculateBounds produce NaN bounds, and the cook then either fails or
                    // builds a meaningless shape - while the spawn log prints a bounds line
                    // and everything downstream reports success.
                    if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                        || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
                        throw new InvalidDataException($"vertex {i} is not finite: {v}");
                    verts[i] = v;
                }

                tris = new int[indexCount];
                for (int i = 0; i < indexCount; i++)
                {
                    var v = r.ReadUInt32();
                    if (v >= (uint)vertCount)
                        throw new InvalidDataException($"index {v} out of range for {vertCount} vertices");
                    tris[i] = (int)v;
                }
            }
        }

        /// <summary>
        /// Waits for the cook to finish, then frees the mesh.
        ///
        /// Destroying a Mesh out from under a running Physics.BakeMesh crashes the process
        /// with no managed stack. The window is real: the bake runs during the spawn stall,
        /// and changing track in that moment despawns the capture.
        ///
        /// Joining, rather than deferring the free to the coroutine. Deferring was written
        /// first and could not work: Unity stops a MonoBehaviour's coroutines when its
        /// GameObject is destroyed, so the "collider is gone, free the mesh" branch was
        /// never reached and every despawn-during-bake leaked the mesh instead - ten
        /// megabytes for drjohnson, plus its cooked PhysX data, for the rest of the session.
        ///
        /// The cost is a main-thread stall for the remainder of a cook that was already
        /// running, only on the path where the capture is being torn down anyway.
        /// </summary>
        private void OnDestroy()
        {
            if (m_Worker != null && m_Worker.IsAlive)
            {
                try { m_Worker.Join(); }
                catch (Exception e) { Debug.LogWarning("[VDGS] bake join failed: " + e.Message); }
            }
            m_Worker = null;

            // The display mesh is a separate copy (CombineMeshes), so it goes on its own.
            if (m_DisplayMesh != null && m_DisplayMesh != m_Mesh)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(m_DisplayMesh);
                else UnityEngine.Object.DestroyImmediate(m_DisplayMesh);
            }
            m_DisplayMesh = null;

            if (m_Mesh == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(m_Mesh);
            else UnityEngine.Object.DestroyImmediate(m_Mesh);
            m_Mesh = null;
        }
    }
}
