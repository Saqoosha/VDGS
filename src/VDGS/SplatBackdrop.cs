using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// A black box enclosing a splat scene, with its faces turned inward.
    ///
    /// A capture has no sky and no ground beyond what was photographed, so out in the
    /// game world the drone sees VelociDrone's terrain, fog and horizon through every
    /// gap in the reconstruction - and captures are full of gaps. Sealing the scene in an
    /// unlit black box removes all of it: the only thing left to see is the capture.
    ///
    /// Inward-facing, so it is invisible from outside and encloses the drone from within.
    /// Opaque and depth-writing, so it occludes the game and the splats composite on top.
    ///
    /// The shader is our own, out of the AssetBundle. Reaching for Shader.Find("Unlit/Color")
    /// would be smaller but it is a bet that the game's build happens to include that
    /// built-in shader, and a missing shader does not raise an error - it renders magenta
    /// or nothing at all, which is exactly the kind of silent wrong this project keeps
    /// getting bitten by.
    /// </summary>
    internal static class SplatBackdrop
    {
        internal const string ChildName = "VDGS_Backdrop";

        /// <summary>
        /// Build (or rebuild) the backdrop under <paramref name="parent"/>.
        ///
        /// Bounds are the splat scene's own, in its local space, so the box inherits the
        /// parent's scale and offset for free.
        /// </summary>
        internal static bool Attach(Transform parent, Vector3 boundsMin, Vector3 boundsMax,
                                    float margin, float groundY, System.Text.StringBuilder log)
        {
            Detach(parent);

            var shader = ShaderBundle.BlackShader;
            if (shader == null)
            {
                log?.AppendLine("backdrop: black shader missing from the bundle");
                return false;
            }

            // Pad outward so the box never slices through splats sitting on the boundary.
            var size = boundsMax - boundsMin;
            var pad = Vector3.one * margin + size * 0.01f;
            var min = boundsMin - pad;
            var max = boundsMax + pad;

            // Captures reach well below their floor - bonsai's bounds run 13 units under
            // it, almost all of that sparse debris - so the padded box sinks through the
            // game's ground plane and its bottom face gets swallowed. Rest the floor of
            // the box just above the world ground instead. The box is a child of the
            // splat object, so the world height has to come back through the parent's
            // position and scale.
            var localGround = parent.InverseTransformPoint(new Vector3(0f, groundY, 0f)).y;
            if (min.y < localGround)
            {
                log?.AppendLine("backdrop: floor raised from " + min.y.ToString("F2")
                                + " to " + localGround.ToString("F2")
                                + " local (world y=" + groundY.ToString("F2") + ")");
                min.y = localGround;
            }

            var go = new GameObject(ChildName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = BuildInwardBox(min, max);

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(shader) { name = "VDGS Backdrop" };
            mr.sharedMaterial.SetColor("_Color", Color.black);
            // Nothing about this box should participate in lighting: it is a void, not a
            // surface. Shadows off both ways, no light probes, no reflection probes.
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            log?.AppendLine("backdrop: box " + min.ToString("F2") + " .. " + max.ToString("F2"));
            return true;
        }

        /// <summary>
        /// Measure, rather than trust, that every face looks at the middle of the box.
        ///
        /// Winding is the one thing here that cannot be reasoned about reliably - the
        /// first version had every triangle backwards and it took seeing it in the game
        /// to notice. A normal pointing at the centre is the definition of "inward", so
        /// check exactly that and say so in the log.
        /// </summary>
        private static void AssertFacesInward(Mesh mesh, Vector3 centre)
        {
            var normals = mesh.normals;
            var verts = mesh.vertices;
            var wrong = 0;
            for (int i = 0; i < verts.Length; i++)
                if (Vector3.Dot(normals[i], centre - verts[i]) <= 0f)
                    wrong++;

            if (wrong > 0)
                VdgsPlugin.Log.LogError("[VDGS] backdrop: " + wrong + " of " + verts.Length +
                                        " vertices face outward - the box will hide the capture");
        }

        internal static void Detach(Transform parent)
        {
            if (parent == null) return;
            var existing = parent.Find(ChildName);
            if (existing == null) return;
            if (Application.isPlaying) Object.Destroy(existing.gameObject);
            else Object.DestroyImmediate(existing.gameObject);
        }

        internal static bool IsAttached(Transform parent)
        {
            return parent != null && parent.Find(ChildName) != null;
        }

        /// <summary>
        /// An axis-aligned box whose triangles wind the other way, so the faces point in.
        ///
        /// Winding rather than Cull Front: culling is the shader's business and this
        /// shader is shared with anything else that wants flat black. Reversed winding
        /// makes the mesh itself inside-out, which is what it actually is.
        /// </summary>
        private static Mesh BuildInwardBox(Vector3 min, Vector3 max)
        {
            var v = new[]
            {
                new Vector3(min.x, min.y, min.z), // 0
                new Vector3(max.x, min.y, min.z), // 1
                new Vector3(max.x, max.y, min.z), // 2
                new Vector3(min.x, max.y, min.z), // 3
                new Vector3(min.x, min.y, max.z), // 4
                new Vector3(max.x, min.y, max.z), // 5
                new Vector3(max.x, max.y, max.z), // 6
                new Vector3(min.x, max.y, max.z), // 7
            };

            // Unity treats CLOCKWISE-as-seen-from-the-front as front-facing, so a face
            // that should be seen from inside the box must be wound counter-clockwise
            // when viewed from outside. Getting this backwards is invisible in code
            // review and obvious the moment it renders - the box turns into a solid
            // black brick hiding the capture instead of a void behind it.
            var tris = new[]
            {
                0, 1, 2,  0, 2, 3,   // -Z
                4, 6, 5,  4, 7, 6,   // +Z
                0, 7, 4,  0, 3, 7,   // -X
                1, 6, 2,  1, 5, 6,   // +X
                0, 5, 1,  0, 4, 5,   // -Y
                3, 6, 7,  3, 2, 6,   // +Y
            };

            var mesh = new Mesh { name = "VDGS Backdrop Box" };
            mesh.vertices = v;
            mesh.triangles = tris;
            mesh.RecalculateNormals();   // follows the winding
            mesh.RecalculateBounds();
            AssertFacesInward(mesh, (min + max) * 0.5f);
            return mesh;
        }
    }
}
