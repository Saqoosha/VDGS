using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VDGS
{
    /// <summary>
    /// Stage 0: prove code injection works and capture the runtime facts that decide
    /// whether a 3D Gaussian Splatting renderer can live inside VelociDrone.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class VdgsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "sh.saqoo.vdgs";
        public const string PluginName = "VDGS";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;

        private System.Collections.Generic.List<SplatScene> m_Scenes = new System.Collections.Generic.List<SplatScene>();
        private bool m_AutoSpawned;
        private PerfLog m_Perf;

        private void Awake()
        {
            Log = Logger;

            // BepInEx disk logging is off by default in 5.4.23, so write our own file
            // next to the game exe where it is trivial to read back over SSH.
            Probe.LogPath = Path.Combine(Paths.GameRootPath, "vdgs-probe.log");
            m_Perf = new PerfLog(Path.Combine(Paths.GameRootPath, "vdgs-perf.log"));
            try { File.WriteAllText(Probe.LogPath, "VDGS " + PluginVersion + " loaded " + DateTime.Now + "\n\n"); }
            catch (Exception e) { Log.LogError("cannot open probe log: " + e.Message); }

            Log.LogInfo("VDGS injected. Probe log: " + Probe.LogPath);
            Probe.Write("Awake");

            LoadShaders();

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>Shaders live in <game>/vdgs/ next to the splat data.</summary>
        private void LoadShaders()
        {
            var dir = Path.Combine(Paths.GameRootPath, "vdgs");
            var report = new StringBuilder();
            report.AppendLine("======== shader bundle @ " + DateTime.Now.ToString("HH:mm:ss.fff") + " ========");

            bool ok;
            try { ok = ShaderBundle.Load(dir, report); }
            catch (Exception e) { ok = false; report.AppendLine("EXCEPTION: " + e); }

            report.AppendLine("=> shaders " + (ok ? "READY" : "NOT READY"));
            report.AppendLine();

            try { File.AppendAllText(Probe.LogPath, report.ToString()); } catch { }
            Log.LogInfo("VDGS shader bundle " + (ok ? "loaded" : "FAILED - see probe log"));
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Cameras are spawned during scene setup; wait a frame so the probe sees them.
            StartCoroutine(ProbeNextFrame(scene.name));
        }

        private System.Collections.IEnumerator ProbeNextFrame(string sceneName)
        {
            yield return null;
            yield return null;
            Probe.Write("sceneLoaded:" + sceneName);

            // Post-process volumes are often created after the scene finishes loading, so
            // a single pass here finds nothing. Sweep a few times over the next seconds.
            StartCoroutine(SweepAutoExposure());

            ApplySceneFilter(sceneName);
        }

        /// <summary>
        /// Spawns the splats that belong in this scene and removes the ones that do not.
        /// Each scene declares its own scene list in placement.json, so a capture meant
        /// for Empty Scene Day does not appear in the middle of a race track.
        /// </summary>
        private void ApplySceneFilter(string sceneName)
        {
            if (!AutoSpawnEnabled() || !IsFlyableScene(sceneName))
                return;

            var report = new StringBuilder();
            report.AppendLine("======== scene filter '" + sceneName + "' @ "
                              + DateTime.Now.ToString("HH:mm:ss.fff") + " ========");
            try
            {
                if (m_Scenes.Count == 0)
                    m_Scenes = SplatScene.Discover(Path.Combine(Paths.GameRootPath, "vdgs"), report);

                foreach (var s in m_Scenes)
                {
                    var wanted = s.WantsScene(sceneName);
                    if (wanted && !s.Spawned)
                    {
                        s.Spawn(report);
                    }
                    else if (!wanted && s.Spawned)
                    {
                        s.Despawn();
                        report.AppendLine(s.Name + ": despawned (not listed for this scene)");
                    }
                    else
                    {
                        report.AppendLine(s.Name + ": " + (wanted ? "already up" : "skipped"));
                    }
                }
            }
            catch (Exception e)
            {
                report.AppendLine("EXCEPTION: " + e);
            }

            report.AppendLine();
            try { File.AppendAllText(Probe.LogPath, report.ToString()); } catch { }
            Log.LogInfo("VDGS scene filter applied for '" + sceneName + "'");
        }

        private System.Collections.IEnumerator SweepAutoExposure()
        {
            var report = new StringBuilder();
            for (int i = 0; i < 6; i++)
            {
                PostProcessFix.DisableAutoExposure(report);
                yield return new WaitForSeconds(2f);
            }
            try { File.AppendAllText(Probe.LogPath, report.ToString()); } catch { }
        }

        /// <summary>Delete &lt;game&gt;/vdgs/autospawn to require pressing F8 instead.</summary>
        private bool AutoSpawnEnabled()
        {
            try { return File.Exists(Path.Combine(Paths.GameRootPath, "vdgs", "autospawn")); }
            catch { return false; }
        }

        /// <summary>
        /// Scenes with no world to place splats into. MainMenu is excluded too: splats
        /// showed up floating behind the menu drone, which looks broken rather than useful.
        /// </summary>
        private static bool IsFlyableScene(string name)
        {
            return name.IndexOf("auth", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("bootstrap", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("NetworkLobby", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private void Update()
        {
            // Manual probe while flying - the interesting camera stack only exists mid-flight.
            if (Input.GetKeyDown(KeyCode.F9))
            {
                Probe.Write("F9 manual");
                Log.LogInfo("VDGS manual probe written");
            }

            // Dump the full GameObject hierarchy of the live scene on demand.
            if (Input.GetKeyDown(KeyCode.F10))
            {
                DumpHierarchy();
            }

            // Numpad 0 mirrors F8 and Numpad Enter mirrors F5, so alignment can be done
            // with one hand on the numpad without reaching for the function row.
            if (Input.GetKeyDown(KeyCode.F8) || Input.GetKeyDown(KeyCode.Keypad0))
            {
                ToggleSplats();
            }

            if (Input.GetKeyDown(KeyCode.F5) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                foreach (var s in m_Scenes) s.SavePlacement();
                Log.LogInfo("VDGS placement saved");
            }

            NudgeSplats();

            if (m_Perf != null)
            {
                int splats = 0, spawned = 0;
                foreach (var s in m_Scenes)
                {
                    if (!s.Spawned) continue;
                    spawned++;
                    splats += s.SplatCount;
                }
                m_Perf.Tick(splats, spawned);
            }
        }

        /// <summary>
        /// Keyboard alignment. A splat capture shares no origin with the track, so the
        /// only practical way to line it up is to look at it and nudge until it fits.
        ///
        /// Numpad only, on purpose: the track editor already owns the arrow keys, and
        /// stealing them made the editor unusable while a splat was loaded.
        ///
        ///   4/6 : X     8/2 : Z     9/3 : Y     7/1 : yaw     +/- : scale
        /// Hold shift for 1m steps instead of 5cm.
        /// </summary>
        private void NudgeSplats()
        {
            if (m_Scenes.Count == 0) return;

            float step = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? 1.0f : 0.05f;
            var move = Vector3.zero;
            if (Input.GetKey(KeyCode.Keypad4)) move.x -= step;
            if (Input.GetKey(KeyCode.Keypad6)) move.x += step;
            if (Input.GetKey(KeyCode.Keypad8)) move.z += step;
            if (Input.GetKey(KeyCode.Keypad2)) move.z -= step;
            if (Input.GetKey(KeyCode.Keypad9)) move.y += step;
            if (Input.GetKey(KeyCode.Keypad3)) move.y -= step;

            float yaw = 0f;
            if (Input.GetKey(KeyCode.Keypad7)) yaw -= step * 10f;
            if (Input.GetKey(KeyCode.Keypad1)) yaw += step * 10f;

            float scale = 0f;
            if (Input.GetKey(KeyCode.KeypadMinus)) scale -= step * 0.1f;
            if (Input.GetKey(KeyCode.KeypadPlus)) scale += step * 0.1f;

            if (move == Vector3.zero && yaw == 0f && scale == 0f)
                return;

            foreach (var s in m_Scenes)
            {
                var tr = s.Transform;
                if (tr == null) continue;
                tr.position += move;
                if (yaw != 0f) tr.Rotate(0f, yaw, 0f, Space.World);
                if (scale != 0f)
                {
                    var v = Mathf.Max(0.01f, tr.localScale.x + scale);
                    tr.localScale = Vector3.one * v;
                }
            }
        }

        private void ToggleSplats()
        {
            var report = new StringBuilder();
            report.AppendLine("======== F8 splats @ " + DateTime.Now.ToString("HH:mm:ss.fff") + " ========");

            try
            {
                if (m_Scenes.Count == 0)
                {
                    m_Scenes = SplatScene.Discover(Path.Combine(Paths.GameRootPath, "vdgs"), report);
                }

                bool anySpawned = m_Scenes.Exists(s => s.Spawned);
                foreach (var s in m_Scenes)
                {
                    if (anySpawned) s.Despawn();
                    else s.Spawn(report);
                }
                report.AppendLine(anySpawned ? "=> despawned" : "=> spawned " + m_Scenes.Count + " scene(s)");
                foreach (var s in m_Scenes) report.AppendLine("  " + s.Describe());
            }
            catch (Exception e)
            {
                report.AppendLine("EXCEPTION: " + e);
            }

            report.AppendLine();
            try { File.AppendAllText(Probe.LogPath, report.ToString()); } catch { }
            Log.LogInfo("VDGS F8 handled - see probe log");
        }

        private void DumpHierarchy()
        {
            try
            {
                var path = Path.Combine(Paths.GameRootPath, "vdgs-hierarchy.txt");
                using (var w = new StreamWriter(path, false))
                {
                    var scene = SceneManager.GetActiveScene();
                    w.WriteLine("scene: " + scene.name + "  " + DateTime.Now);
                    foreach (var root in scene.GetRootGameObjects())
                        WriteNode(w, root.transform, 0);
                }
                Log.LogInfo("VDGS hierarchy dumped: " + path);
            }
            catch (Exception e) { Log.LogError("hierarchy dump failed: " + e); }
        }

        private static void WriteNode(StreamWriter w, Transform t, int depth)
        {
            // Deep scenes are huge; the shallow layers are what we need to place a splat root.
            if (depth > 4) return;

            var comps = t.GetComponents<Component>();
            var names = new string[comps.Length];
            for (int i = 0; i < comps.Length; i++)
                names[i] = comps[i] == null ? "<missing>" : comps[i].GetType().Name;

            w.WriteLine(new string(' ', depth * 2) + t.name
                + "  [" + string.Join(", ", names) + "]"
                + (t.gameObject.activeInHierarchy ? "" : "  (inactive)"));

            for (int i = 0; i < t.childCount; i++)
                WriteNode(w, t.GetChild(i), depth + 1);
        }
    }
}
