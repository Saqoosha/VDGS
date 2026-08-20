using System;
using System.Text;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// Draws the collision mesh, so it can be looked at from inside the game.
    ///
    /// A MeshCollider renders nothing. Every check on this shape so far has been numeric or
    /// done in a browser preview, and both have now missed defects that one look from a
    /// drone's eye would have caught - textilni passed a visual review in the preview and
    /// then swallowed every dropped ball.
    ///
    /// Two modes, because they answer different questions:
    ///
    ///   solid   translucent, BACK FACES CULLED. This is the orientation test: from inside a
    ///           correctly wound room you see the walls; from inside an inside-out one you
    ///           see nothing at all, because every surface facing you has been culled.
    ///   wire    every triangle edge, no culling, no depth write. Shows structure the solid
    ///           pass hides - stacked sheets, interpenetration, how coarse the mesh is.
    ///
    /// Deliberately unlit: the mesh carries no normals, so anything lit renders black.
    /// </summary>
    internal class SplatCollisionView : MonoBehaviour
    {
        internal const string kOff = "off";
        internal const string kSolid = "solid";
        internal const string kWire = "wire";

        private static readonly Color kSolidColor = new Color(0.25f, 0.9f, 1f, 0.22f);
        private static readonly Color kWireColor = new Color(0.35f, 1f, 1f, 0.85f);

        private Mesh m_Lines;
        private MeshFilter m_Filter;
        private MeshRenderer m_Renderer;
        private Material m_Material;
        private string m_Mode = kOff;

        internal static bool IsMode(string mode)
        {
            return mode == kOff || mode == kSolid || mode == kWire;
        }

        /// <summary>Current mode for the collider under <paramref name="parent"/>.</summary>
        internal static string ModeOn(Transform parent)
        {
            var view = Find(parent);
            return view != null ? view.m_Mode : kOff;
        }

        /// <summary>
        /// Switches what is drawn. Returns false when nothing was drawn - no collider, no
        /// baked mesh yet, or no usable shader. Only the middle one is worth retrying.
        ///
        /// The view is created on demand and kept afterwards, so flipping between modes
        /// while flying costs nothing - only the first switch to wire builds anything.
        /// </summary>
        internal static bool SetMode(Transform parent, string mode, StringBuilder log)
        {
            if (!IsMode(mode))
            {
                log?.AppendLine("  unknown collision view '" + mode + "'");
                return false;
            }

            var child = parent.Find(SplatCollision.CollisionChildName(parent.name));
            var col = child != null ? child.GetComponent<MeshCollider>() : null;
            if (col == null)
            {
                log?.AppendLine("  no collider to draw - is collision on?");
                return false;
            }

            var view = child.GetComponent<SplatCollisionView>() ?? child.gameObject.AddComponent<SplatCollisionView>();
            return view.Apply(col, mode, log);
        }

        private static SplatCollisionView Find(Transform parent)
        {
            var child = parent.Find(SplatCollision.CollisionChildName(parent.name));
            return child != null ? child.GetComponent<SplatCollisionView>() : null;
        }

        /// <summary>
        /// Returns false when nothing was drawn, so a caller can retry.
        ///
        /// Reporting success unconditionally is what a first version did, and it broke the
        /// retry it existed to serve: SplatScene clears its pending view on a true, so a
        /// view asked for at spawn - before the off-thread cook finishes - was dropped for
        /// good rather than applied a frame later.
        /// </summary>
        private bool Apply(MeshCollider col, string mode, StringBuilder log)
        {
            if (mode == kOff)
            {
                if (m_Renderer != null) m_Renderer.enabled = false;
                m_Mode = kOff;
                log?.AppendLine("  collision view off");
                return true;
            }

            var solidMesh = col.sharedMesh;
            if (solidMesh == null)
            {
                // The cook runs off-thread, so the mesh can still be missing for a frame or
                // two after a spawn. Saying so beats drawing nothing and looking broken.
                log?.AppendLine("  collision mesh not baked yet - try again in a moment");
                return false;
            }

            if (!EnsureMaterial(log)) return false;

            if (m_Filter == null) m_Filter = gameObject.AddComponent<MeshFilter>();
            if (m_Renderer == null)
            {
                m_Renderer = gameObject.AddComponent<MeshRenderer>();
                m_Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_Renderer.receiveShadows = false;
                m_Renderer.sharedMaterial = m_Material;
            }
            m_Renderer.enabled = true;

            if (mode == kWire)
            {
                if (m_Lines == null) m_Lines = BuildLines(solidMesh, log);
                if (m_Lines == null) return false;
                m_Filter.sharedMesh = m_Lines;
                SetPass(cull: 0, color: kWireColor);          // 0 = Off, lines have no facing
                log?.AppendLine("  collision view: wire");
            }
            else
            {
                m_Filter.sharedMesh = solidMesh;
                SetPass(cull: 2, color: kSolidColor);         // 2 = Back
                log?.AppendLine("  collision view: solid, back faces culled");
            }

            // Only now, on the path that actually drew something. Recording it up front let
            // the status API and placement.json claim a view that was never applied.
            m_Mode = mode;
            return true;
        }

        /// <summary>
        /// A material that can be built at runtime inside a game nobody compiled shaders for.
        ///
        /// Hidden/Internal-Colored is one of Unity's always-included shaders - it is what the
        /// editor's own debug drawing uses - so it is present in a shipped player even though
        /// the game never referenced it. It exposes blending, culling and depth as float
        /// properties, which is exactly the control this needs. The splat shader bundle is no
        /// help here: it holds the gaussian shaders and rebuilding it needs the Windows box.
        /// </summary>
        private bool EnsureMaterial(StringBuilder log)
        {
            if (m_Material != null) return true;

            // No fallback, on purpose. Sprites/Default and Unlit/Color have no _Cull, so
            // SetPass below would silently no-op and `solid` would draw every face - and
            // `solid` is the orientation test: "you see nothing from inside" is what says
            // the shell is inside-out. A fallback turns that check into a permanent pass,
            // which in a project whose rule is that visual checks never caught a defect is
            // worse than drawing nothing at all.
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                log?.AppendLine("  Hidden/Internal-Colored is missing from this build - "
                                + "cannot draw the collision mesh, and no substitute can "
                                + "cull back faces, so the orientation test would lie");
                Debug.LogWarning("[VDGS] no Hidden/Internal-Colored; collision view unavailable");
                return false;
            }

            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            // The reason the fallbacks went is that they have no _Cull, so SetPass would
            // no-op and `solid` would draw every face - turning the orientation test into a
            // permanent pass. That argument applies to whatever shader is found here too.
            if (!mat.HasProperty("_Cull"))
            {
                log?.AppendLine("  " + shader.name + " has no _Cull - back faces could not be "
                                + "culled, so the orientation test would lie. Not drawing.");
                UnityEngine.Object.Destroy(mat);
                return false;
            }

            m_Material = mat;
            return true;
        }

        private void SetPass(int cull, Color color)
        {
            m_Material.SetColor("_Color", color);
            m_Material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m_Material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m_Material.SetInt("_Cull", cull);
            m_Material.SetInt("_ZWrite", 0);
            m_Material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);

            // Transparent queue, or it draws before the scene and gets overwritten.
            m_Material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        /// <summary>
        /// The same vertices, indexed as one line per triangle edge.
        ///
        /// Shared edges are emitted twice. Deduplicating would halve the buffer and cost a
        /// hash of every edge on a mesh that runs to half a million triangles; this only
        /// exists to be looked at, and the buffer is built once.
        /// </summary>
        private static Mesh BuildLines(Mesh src, StringBuilder log)
        {
            try
            {
                var tris = src.triangles;
                var verts = src.vertices;

                var idx = new int[tris.Length * 2];
                for (int t = 0, o = 0; t < tris.Length; t += 3, o += 6)
                {
                    idx[o + 0] = tris[t + 0]; idx[o + 1] = tris[t + 1];
                    idx[o + 2] = tris[t + 1]; idx[o + 3] = tris[t + 2];
                    idx[o + 4] = tris[t + 2]; idx[o + 5] = tris[t + 0];
                }

                var mesh = new Mesh { name = src.name + "_wire" };
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(verts);
                mesh.SetIndices(idx, MeshTopology.Lines, 0, false);
                mesh.bounds = src.bounds;
                log?.AppendLine("  wire mesh: " + (idx.Length / 2).ToString("N0") + " segments");
                return mesh;
            }
            catch (Exception e)
            {
                log?.AppendLine("  wire mesh failed: " + e.Message);
                return null;
            }
        }

        private void OnDestroy()
        {
            // Destroy does nothing in edit mode, and the wire buffer is ~12 MB on a 500K
            // triangle mesh. Same split SplatCollision.OnDestroy makes, for the same reason.
            if (Application.isPlaying)
            {
                if (m_Lines != null) UnityEngine.Object.Destroy(m_Lines);
                if (m_Material != null) UnityEngine.Object.Destroy(m_Material);
            }
            else
            {
                if (m_Lines != null) UnityEngine.Object.DestroyImmediate(m_Lines);
                if (m_Material != null) UnityEngine.Object.DestroyImmediate(m_Material);
            }
            m_Lines = null;
            m_Material = null;
        }
    }
}
