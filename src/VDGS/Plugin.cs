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
        private PerfLog m_Perf;

        private TrackBindings m_Bindings;
        private string m_CurrentTrack;
        private float m_TrackPollTimer;
        private string m_TrackLogPath;
        private WebControl m_Web;

        private void Awake()
        {
            Log = Logger;

            // BepInEx disk logging is off by default in 5.4.23, so write our own file
            // next to the game exe where it is trivial to read back over SSH.
            Probe.LogPath = Path.Combine(Paths.GameRootPath, "vdgs-probe.log");
            m_Perf = new PerfLog(Path.Combine(Paths.GameRootPath, "vdgs-perf.log"));
            m_Bindings = new TrackBindings(Path.Combine(Paths.GameRootPath, "vdgs", "bindings.json"));
            m_TrackLogPath = Path.Combine(Paths.GameRootPath, "vdgs-track.log");
            try { File.WriteAllText(m_TrackLogPath, "track watch started " + DateTime.Now + "\n"); } catch { }
            try { File.WriteAllText(Probe.LogPath, "VDGS " + PluginVersion + " loaded " + DateTime.Now + "\n\n"); }
            catch (Exception e) { Log.LogError("cannot open probe log: " + e.Message); }

            Log.LogInfo("VDGS injected. Probe log: " + Probe.LogPath);
            Probe.Write("Awake");

            LoadShaders();
            StartWebControl();

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>
        /// Control lives in a browser rather than in-game keys: every key tried so far
        /// collided with the game (F7 saves the scene, arrows drive the track editor,
        /// numpad does not exist on a laptop), and the game offers nowhere to draw a HUD
        /// so an in-game workflow gives no feedback at all.
        /// </summary>
        private void StartWebControl()
        {
            var report = new StringBuilder();
            try
            {
                m_Web = new WebControl
                {
                    UiRoot = Path.Combine(Paths.GameRootPath, "vdgs", VdgsPaths.UiDirName),
                    StatusProvider = BuildStatus,
                    LoadSplat = ShowOnly,
                    BindCurrent = BindTo,
                    UnbindTrack = Unbind,
                    SetTransform = ApplyTransform,
                    SetBackdrop = ApplyBackdrop,
                    SetCollision = ApplyCollision,
                    SetCollisionView = ApplyCollisionView,
                };
                if (!m_Web.Start(WebControl.kDefaultPort, report))
                {
                    m_Web.Dispose();
                    m_Web = null;
                }
            }
            catch (Exception e)
            {
                report.AppendLine("web control failed: " + e);
                m_Web = null;
            }

            try { File.AppendAllText(Probe.LogPath, report.ToString()); } catch { }
            Log.LogInfo(m_Web != null
                ? "VDGS web control: " + m_Web.Url
                : "VDGS web control unavailable - see probe log");
        }

        /// <summary>Snapshot for the browser UI. Runs on the main thread.</summary>
        private object BuildStatus()
        {
            EnsureDiscovered();

            var available = new System.Collections.Generic.List<object>();
            var loaded = new System.Collections.Generic.List<string>();
            foreach (var s in m_Scenes)
            {
                available.Add(new System.Collections.Generic.Dictionary<string, object>
                {
                    { "name", s.Name },
                    { "source", s.Source },
                    { "kind", s.Kind },
                    { "splats", s.SplatCount },
                    { "posFormat", s.PosFormat },
                    { "scaleFormat", s.ScaleFormat },
                    { "colorFormat", s.ColorFormat },
                    { "shFormat", s.ShFormat },
                    { "bytes", s.Bytes },
                    { "shown", s.Spawned },
                    { "scale", s.Scale },
                    { "y", s.YOffset },
                    { "backdrop", s.BackdropOn },
                    // Two fields, not one: the UI must be able to tell "no collision mesh
                    // generated" apart from "mesh generated and switched off", or a missing
                    // file reads as a setting somebody turned off.
                    { "hasCollision", s.HasCollision },
                    { "collision", s.CollisionOn },
                    { "collisionView", s.CollisionView },
                });
                if (s.Spawned) loaded.Add(s.Name);
            }

            return new System.Collections.Generic.Dictionary<string, object>
            {
                { "track", m_CurrentTrack },
                { "loaded", loaded },
                { "available", available },
                { "bindings", m_Bindings.All() },
            };
        }

        /// <summary>Encloses one capture in a black box, or removes it.</summary>
        private void ApplyBackdrop(string name, bool on)
        {
            EnsureDiscovered();
            var log = new StringBuilder();
            foreach (var s in m_Scenes)
            {
                if (!string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                s.SetBackdrop(on, log);
            }
            if (log.Length > 0)
            {
                try { File.AppendAllText(Probe.LogPath, log.ToString()); } catch { }
            }
        }

        /// <summary>Makes one capture solid, or lets the drone fly through it.</summary>
        private void ApplyCollision(string name, bool on)
        {
            EnsureDiscovered();
            var log = new StringBuilder();
            foreach (var s in m_Scenes)
            {
                if (!string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                s.SetCollision(on, log);
            }
            if (log.Length > 0)
            {
                try { File.AppendAllText(Probe.LogPath, log.ToString()); } catch { }
            }
        }

        /// <summary>Draws the collision mesh for one capture: off, solid or wire.</summary>
        private void ApplyCollisionView(string name, string mode)
        {
            EnsureDiscovered();
            var log = new StringBuilder();
            foreach (var s in m_Scenes)
            {
                if (!string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                s.SetCollisionView(mode, log);
            }
            if (log.Length > 0)
            {
                try { File.AppendAllText(Probe.LogPath, log.ToString()); } catch { }
            }
        }

        /// <summary>Shows exactly one splat scene, or none when name is null/empty.</summary>
        private void ShowOnly(string name)
        {
            EnsureDiscovered();

            var log = new StringBuilder();
            log.AppendLine("======== show '" + (name ?? "none") + "' @ "
                           + DateTime.Now.ToString("HH:mm:ss") + " ========");

            foreach (var s in m_Scenes)
            {
                var want = !string.IsNullOrEmpty(name) && s.Name == name;
                if (want && !s.Spawned) s.Spawn(log);
                else if (!want && s.Spawned) { s.Despawn(); log.AppendLine(s.Name + ": despawned"); }
            }

            try { File.AppendAllText(Probe.LogPath, log.ToString()); } catch { }
        }

        /// <summary>Binds the given splats to whatever track is loaded right now.</summary>
        private void BindTo(string[] splats)
        {
            var log = new StringBuilder();
            log.AppendLine("======== bind @ " + DateTime.Now.ToString("HH:mm:ss") + " ========");

            var track = m_CurrentTrack;
            if (string.IsNullOrEmpty(track))
            {
                try { track = TrackName.Current(log); } catch { }
            }

            if (string.IsNullOrEmpty(track))
                log.AppendLine("no track loaded - nothing to bind to");
            else
            {
                m_Bindings.Set(track, splats ?? new string[0], log);
                log.Append(m_Bindings.Describe());
            }

            log.AppendLine();
            try { File.AppendAllText(m_TrackLogPath, log.ToString()); } catch { }
        }

        /// <summary>Resizes / raises a capture. Applies live and writes placement.json.</summary>
        private void ApplyTransform(string name, float? scale, float? y)
        {
            EnsureDiscovered();
            var log = new StringBuilder();
            log.AppendLine("======== transform @ " + DateTime.Now.ToString("HH:mm:ss") + " ========");

            var hit = false;
            foreach (var s in m_Scenes)
            {
                if (!string.IsNullOrEmpty(name) && s.Name != name) continue;
                s.SetTransform(scale, y, log);
                hit = true;
            }
            if (!hit) log.AppendLine("no splat named '" + (name ?? "-") + "'");

            try { File.AppendAllText(Probe.LogPath, log.ToString()); } catch { }
        }

        /// <summary>Removes a track's binding; null means the track loaded right now.</summary>
        private void Unbind(string track)
        {
            var log = new StringBuilder();
            log.AppendLine("======== unbind @ " + DateTime.Now.ToString("HH:mm:ss") + " ========");

            var target = string.IsNullOrEmpty(track) ? m_CurrentTrack : track;
            if (string.IsNullOrEmpty(target))
                log.AppendLine("no track given and none loaded");
            else
            {
                m_Bindings.Remove(target, log);
                log.Append(m_Bindings.Describe());

                // Nothing should stay on screen for a track that is no longer bound.
                if (string.Equals(target, m_CurrentTrack, StringComparison.OrdinalIgnoreCase))
                    foreach (var s in m_Scenes)
                        if (s.Spawned) s.Despawn();
            }

            log.AppendLine();
            try { File.AppendAllText(m_TrackLogPath, log.ToString()); } catch { }
        }

        private void EnsureDiscovered()
        {
            if (m_Scenes.Count != 0) return;
            var report = new StringBuilder();
            m_Scenes = SplatScene.Discover(Path.Combine(Paths.GameRootPath, "vdgs"), report);
            try { File.AppendAllText(Probe.LogPath, report.ToString()); } catch { }
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
            m_Web?.Dispose();
            m_Web = null;
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

            // Spawning is driven entirely by which track is loaded (see PollTrack), not
            // by which Unity scene it happens to sit on. A scenery hosts many tracks, so
            // a scene-level filter would show a capture on every track sharing that map.
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

            // Hunt for whatever the game uses to identify the loaded track. Everything
            // about binding a splat to a track depends on being able to read that name,
            // and Assembly-CSharp is obfuscated, so it has to be found at runtime.
            //
            // F12, not F7: the track editor already binds F7 to saving the scene.
            // The needle comes from <game>/vdgs/needle.txt so the search string can be
            // changed without rebuilding the plugin.
            if (Input.GetKeyDown(KeyCode.F12))
            {
                var needlePath = Path.Combine(Paths.GameRootPath, "vdgs", "needle.txt");
                string needle = null;
                try { if (File.Exists(needlePath)) needle = File.ReadAllText(needlePath).Trim(); }
                catch { }

                TrackProbe.Dump(Path.Combine(Paths.GameRootPath, "vdgs-track.txt"), needle);
                Log.LogInfo("VDGS track probe written (needle: " + (needle ?? "none") + ")");
            }

            m_Web?.Pump();
            PollTrack();

            if (m_Perf != null)
            {
                int splats = 0, spawned = 0;
                string shown = null;
                foreach (var s in m_Scenes)
                {
                    if (!s.Spawned) continue;
                    spawned++;
                    splats += s.SplatCount;
                    shown = shown == null ? s.Name : shown + "," + s.Name;
                }
                m_Perf.Tick(splats, spawned, shown);
            }
        }

        /// <summary>
        /// Watches which track is loaded and swaps splats to match.
        ///
        /// Polled rather than event-driven: the game can change track without changing
        /// Unity scene (the in-game "change track" dialog does exactly that), so
        /// sceneLoaded is not enough on its own.
        /// </summary>
        private void PollTrack()
        {
            m_TrackPollTimer += Time.unscaledDeltaTime;
            if (m_TrackPollTimer < 1f) return;
            m_TrackPollTimer = 0f;

            var log = new StringBuilder();
            string name;
            try { name = TrackName.Current(log); }
            catch (Exception e) { log.AppendLine("track read failed: " + e.Message); name = null; }

            if (!string.Equals(name, m_CurrentTrack, StringComparison.Ordinal))
            {
                log.AppendLine(DateTime.Now.ToString("HH:mm:ss") + "  track changed: '"
                               + (m_CurrentTrack ?? "-") + "' -> '" + (name ?? "-") + "'");
                m_CurrentTrack = name;
                ApplyTrackBinding(name, log);
            }

            // A view asked for at spawn time could not be applied then - the collider's mesh
            // is cooked on a worker thread and is null for the first frames. Retried here so
            // the drawing appears on its own rather than needing a second click.
            foreach (var s in m_Scenes) s.PumpPendingView(log);

            if (log.Length > 0)
            {
                try { File.AppendAllText(m_TrackLogPath, log.ToString()); } catch { }
            }
        }

        /// <summary>Spawns exactly the splats bound to this track; despawns the rest.</summary>
        private void ApplyTrackBinding(string track, StringBuilder log)
        {
            if (!AutoSpawnEnabled()) return;

            if (m_Scenes.Count == 0)
                m_Scenes = SplatScene.Discover(Path.Combine(Paths.GameRootPath, "vdgs"), log);

            // An unbound track gets nothing: better to show no splats than the wrong ones.
            var wanted = m_Bindings.For(track);
            if (!m_Bindings.Has(track))
            {
                log.AppendLine("  no binding for '" + (track ?? "-") + "' - leaving splats alone");
                return;
            }

            foreach (var s in m_Scenes)
            {
                var want = wanted.Contains(s.Name);
                if (want && !s.Spawned) s.Spawn(log);
                else if (!want && s.Spawned) { s.Despawn(); log.AppendLine("  " + s.Name + ": despawned"); }
            }
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
