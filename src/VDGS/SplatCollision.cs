using System;
using System.Collections;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// The collision surface for a splat scene: a MeshCollider built at runtime from
    /// collision.bin.
    ///
    /// A capture is a cloud of oriented blobs with no triangles, so the drone flies
    /// through walls and falls through floors. The shape is generated offline (OpenVDB
    /// level set - see docs/superpowers/specs/2026-08-18-splat-collision-design.md) and
    /// arrives as vertices and triangle indices, nothing else. Everything the collision
    /// then does - deformation, impact sound, crash detection - is the game's own code
    /// reacting to a collider being there.
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
            return dir.EndsWith(".ply", StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(dir, ".collision.bin")
                : Path.Combine(dir, "collision.bin");
        }

        /// <summary>
        /// Adds the collider under <paramref name="parent"/>, or reports why not.
        ///
        /// Returns false when there is no collision.bin, which is the normal case for a
        /// capture nobody has generated one for - not an error.
        /// </summary>
        internal static bool Attach(Transform parent, string dir, StringBuilder log)
        {
            var path = PathFor(dir);
            if (!File.Exists(path))
            {
                log.AppendLine("  no collision.bin at " + path);
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
            // Done here rather than in the offline tool on purpose: the reflection is a
            // property of how the runtime reads a .ply, so it belongs next to the code that
            // does it. Put it in the pipeline instead and the two drift apart - which is what
            // happened, and it cost a flight to notice.
            var mirror = dir.EndsWith(".ply", StringComparison.OrdinalIgnoreCase);
            if (self.Load(path, mirror, log)) return true;

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
            return child != null && child.GetComponent<MeshCollider>() != null;
        }

        /// <summary>True when a collision mesh has been generated for this capture.</summary>
        internal static bool Exists(string dir)
        {
            return File.Exists(PathFor(dir));
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
            var col = ColliderOn(parent);
            if (col == null) return false;
            col.enabled = on;
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
            var col = ColliderOn(parent);
            return col != null && col.enabled;
        }

        private static MeshCollider ColliderOn(Transform parent)
        {
            var child = parent.Find(CollisionChildName(parent.name));
            return child != null ? child.GetComponent<MeshCollider>() : null;
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

            if (m_Mesh == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(m_Mesh);
            else UnityEngine.Object.DestroyImmediate(m_Mesh);
            m_Mesh = null;
        }
    }
}
