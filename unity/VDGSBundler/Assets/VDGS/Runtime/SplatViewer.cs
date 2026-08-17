using System.IO;
using System.Text;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// Loads a converted splat scene in the editor, using the exact same code the
    /// injected plugin runs.
    ///
    /// This exists because the round trip through the game is far too slow to debug
    /// with: convert on the Mac, ship hundreds of megabytes to the Windows box, start
    /// VelociDrone, look, discover the orientation or the quaternions are wrong, repeat.
    /// A mistake that takes ten seconds to see here took ten minutes to see there, and
    /// most of a day was lost to that gap.
    ///
    /// Same SplatData, same SplatRenderer, same shaders, same Unity version as the game,
    /// so anything that looks right here will look right in the sim.
    /// </summary>
    [ExecuteInEditMode]
    public class SplatViewer : MonoBehaviour
    {
        [Tooltip("Folder holding meta.json and the five .bin files")]
        public string m_Directory = "../../build/splats/playroom";

        [Header("Shaders (from Assets/VDGS/Shaders)")]
        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public ComputeShader m_CSSplatUtilities;

        [Header("Placement")]
        public float m_Scale = 1f;
        public Vector3 m_Offset = Vector3.zero;

        [Header("Status (read-only)")]
        public string m_Status = "not loaded";
        public int m_SplatCount;
        public Vector3 m_BoundsMin, m_BoundsMax;

        private SplatRenderer m_Renderer;
        private string m_Loaded;

        private void OnEnable() => Reload();

        [ContextMenu("Reload")]
        public void Reload()
        {
            Unload();

            var dir = ResolvePath(m_Directory);
            if (!Directory.Exists(dir))
            {
                m_Status = "directory not found: " + dir;
                Debug.LogWarning("[VDGS] " + m_Status);
                return;
            }

            var data = SplatData.Load(dir, out var error);
            if (data == null)
            {
                m_Status = "load failed: " + error;
                Debug.LogError("[VDGS] " + m_Status);
                return;
            }

            m_SplatCount = data.SplatCount;
            m_BoundsMin = data.BoundsMin;
            m_BoundsMax = data.BoundsMax;

            m_Renderer = gameObject.GetComponent<SplatRenderer>();
            if (m_Renderer == null)
                m_Renderer = gameObject.AddComponent<SplatRenderer>();

            m_Renderer.m_ShaderSplats = m_ShaderSplats;
            m_Renderer.m_ShaderComposite = m_ShaderComposite;
            m_Renderer.m_CSSplatUtilities = m_CSSplatUtilities;
            m_Renderer.SetData(data);

            transform.localScale = Vector3.one * m_Scale;
            transform.position = m_Offset;

            m_Loaded = dir;
            var size = m_BoundsMax - m_BoundsMin;
            m_Status = string.Format("{0:N0} splats, {1:0.0} x {2:0.0} x {3:0.0}",
                data.SplatCount, size.x, size.y, size.z);
            Debug.Log("[VDGS] loaded " + dir + ": " + m_Status);
        }

        [ContextMenu("Unload")]
        public void Unload()
        {
            var r = gameObject.GetComponent<SplatRenderer>();
            if (r != null)
            {
                if (Application.isPlaying) Destroy(r); else DestroyImmediate(r);
            }
            m_Renderer = null;
            m_Loaded = null;
            m_Status = "not loaded";
        }

        private void Update()
        {
            if (m_Renderer == null) return;
            // Live tweaks without a reload, matching what the in-game control panel does.
            transform.localScale = Vector3.one * Mathf.Max(0.001f, m_Scale);
            transform.position = m_Offset;
        }

        /// <summary>Relative paths resolve against the project folder, not Assets/.</summary>
        private static string ResolvePath(string p)
        {
            if (Path.IsPathRooted(p)) return p;
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.GetFullPath(Path.Combine(projectRoot ?? ".", p));
        }
    }
}
