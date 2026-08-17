using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// Owns the splat objects placed into the game's scene, and the keyboard controls
    /// used to line them up with the track. Placement is done by eye in-game because
    /// a splat capture has no shared origin with the VelociDrone scenery.
    ///
    /// The transform of each scene is persisted to placement.json next to its data, so
    /// alignment survives a restart.
    /// </summary>
    internal class SplatScene
    {
        private readonly string m_Dir;
        private GameObject m_Go;
        private SplatRenderer m_Renderer;

        internal string Name { get; }
        internal bool Spawned => m_Go != null;

        [Serializable]
        private class Placement
        {
            public float[] position = { 0, 0, 0 };
            public float[] rotation = { 0, 0, 0 };
            public float scale = 1f;

            /// <summary>
            /// Unity scene names this splat should auto-spawn in. Empty means every
            /// flyable scene. VelociDrone's scene names come from the `sceneries` table
            /// in settings.db - "BlankCanvas" is Empty Scene Day, "BlankCanvasNight" is
            /// Empty Scene Night. See AGENTS.md for the full list.
            /// </summary>
            public string[] scenes = new string[0];
        }

        private string[] m_Scenes = new string[0];

        /// <summary>True if this splat wants to appear in the named Unity scene.</summary>
        internal bool WantsScene(string sceneName)
        {
            if (m_Scenes == null || m_Scenes.Length == 0)
                return true;
            foreach (var s in m_Scenes)
                if (string.Equals(s, sceneName, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        internal SplatScene(string dir)
        {
            m_Dir = dir;
            Name = new DirectoryInfo(dir).Name;
            // Read placement up front: the scene filter has to answer before we spawn.
            m_Scenes = LoadPlacement().scenes ?? new string[0];
        }

        /// <summary>Finds every splat scene directory under &lt;game&gt;/vdgs/.</summary>
        internal static List<SplatScene> Discover(string vdgsDir, StringBuilder report)
        {
            var found = new List<SplatScene>();
            if (!Directory.Exists(vdgsDir))
            {
                report.AppendLine("no vdgs dir at " + vdgsDir);
                return found;
            }

            foreach (var dir in Directory.GetDirectories(vdgsDir))
            {
                if (!File.Exists(Path.Combine(dir, "meta.json")))
                    continue;
                found.Add(new SplatScene(dir));
                report.AppendLine("found splat scene: " + new DirectoryInfo(dir).Name);
            }

            if (found.Count == 0)
                report.AppendLine("no splat scenes under " + vdgsDir + " (need <name>/meta.json)");
            return found;
        }

        internal bool Spawn(StringBuilder report)
        {
            if (m_Go != null)
            {
                report.AppendLine(Name + ": already spawned");
                return true;
            }

            var data = SplatData.Load(m_Dir, out var error);
            if (data == null)
            {
                report.AppendLine(Name + ": load failed - " + error);
                return false;
            }
            report.AppendLine(Name + ": " + data.Describe());

            m_Go = new GameObject("VDGS_" + Name);
            UnityEngine.Object.DontDestroyOnLoad(m_Go);

            var placement = LoadPlacement();
            m_Scenes = placement.scenes ?? new string[0];
            m_Go.transform.position = new Vector3(placement.position[0], placement.position[1], placement.position[2]);
            m_Go.transform.eulerAngles = new Vector3(placement.rotation[0], placement.rotation[1], placement.rotation[2]);
            m_Go.transform.localScale = Vector3.one * placement.scale;

            m_Renderer = m_Go.AddComponent<SplatRenderer>();
            m_Renderer.SetData(data);

            report.AppendLine(Name + ": spawned at " + m_Go.transform.position
                              + " rot " + m_Go.transform.eulerAngles
                              + " scale " + placement.scale);
            return true;
        }

        internal void Despawn()
        {
            if (m_Go == null)
                return;
            UnityEngine.Object.Destroy(m_Go);
            m_Go = null;
            m_Renderer = null;
        }

        internal Transform Transform => m_Go != null ? m_Go.transform : null;
        internal int SplatCount => m_Renderer != null ? m_Renderer.SplatCount : 0;

        internal void SavePlacement()
        {
            if (m_Go == null)
                return;
            var tr = m_Go.transform;
            var p = new Placement
            {
                position = new[] { tr.position.x, tr.position.y, tr.position.z },
                rotation = new[] { tr.eulerAngles.x, tr.eulerAngles.y, tr.eulerAngles.z },
                scale = tr.localScale.x,
                // Preserve the scene filter; saving position must not widen where it spawns.
                scenes = m_Scenes,
            };
            try { File.WriteAllText(Path.Combine(m_Dir, "placement.json"), JsonUtility.ToJson(p, true)); }
            catch (Exception e) { VdgsPlugin.Log.LogError("placement save failed: " + e.Message); }
        }

        private Placement LoadPlacement()
        {
            var path = Path.Combine(m_Dir, "placement.json");
            if (!File.Exists(path))
                return new Placement();
            try
            {
                var p = JsonUtility.FromJson<Placement>(File.ReadAllText(path));
                // JsonUtility leaves arrays null when the field is missing from the file.
                if (p == null) return new Placement();
                if (p.position == null || p.position.Length < 3) p.position = new float[] { 0, 0, 0 };
                if (p.rotation == null || p.rotation.Length < 3) p.rotation = new float[] { 0, 0, 0 };
                if (p.scale <= 0f) p.scale = 1f;
                return p;
            }
            catch
            {
                return new Placement();
            }
        }

        internal string Describe()
        {
            if (m_Go == null) return Name + " (not spawned)";
            var tr = m_Go.transform;
            return string.Format("{0} pos={1} rot={2} scale={3:0.###} splats={4}",
                Name, tr.position, tr.eulerAngles, tr.localScale.x,
                m_Renderer != null ? m_Renderer.SplatCount : 0);
        }
    }
}
