using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VDGS
{
    /// <summary>
    /// Turns the game's own world black - no ground plane, no sky - while leaving its
    /// colliders in place.
    ///
    /// A capture that has no collision mesh still needs a floor to land on, and the game
    /// already has one: BlankCanvas (Empty Scene Day) is a root object `Terrain` holding
    /// `Plane` (MeshRenderer + BoxCollider) and an inactive `Terrain`. Disabling the
    /// Renderer and not the GameObject keeps the BoxCollider solid; a Unity Terrain is
    /// not a Renderer, so Terrain components are switched off the same way, leaving their
    /// TerrainCollider. The sky is the flight camera's clear mode (Skybox, drawing
    /// RenderSettings.skybox); `Sky Dome` is inactive in the scene, so switching every
    /// enabled camera to a solid black clear is the whole sky. Fog is not touched: it is
    /// off in BlankCanvas, the only scenery this has been used on.
    ///
    /// <see cref="SplatBackdrop"/> hides the world by boxing the capture in, which only
    /// works while the capture is upright and only within the box; this hides the world
    /// itself, so it holds for a rotated capture and out past the capture's bounds too.
    ///
    /// World state, not capture state: several captures can be up at once and the world
    /// has to stay dark while any of them asks for it, so wanters are counted by name and
    /// the change is applied once, on the first, and undone once, on the last. Scene
    /// objects are per scene - a restart reloads the scene and brings a fresh, visible
    /// Plane - so <see cref="Reapply"/> runs from sceneLoaded while anyone still wants it.
    ///
    /// Apply is idempotent and only ever adds to the records: an object already recorded is
    /// left alone, and a renderer that is off but not recorded is not ours to restore.
    /// That matters because sceneLoaded also fires for additive loads (the track editor
    /// lands on top of the flight scene) and a spawn can beat the deferred Reapply to the
    /// new objects - if Reapply cleared the records, the Plane would already be off, get
    /// skipped, and never be restored.
    ///
    /// Every loaded scene is searched, not the active one: the flight scene is loaded
    /// alongside the menu and does not necessarily become active (see Plugin.PollTrack).
    /// And the same idempotent sweep runs once a second from PollTrack, because the game
    /// enables its ground and flight camera without a scene load when the track editor
    /// hands over to flight - a capture spawned in the editor saw neither.
    /// </summary>
    internal static class WorldBlackout
    {
        /// <summary>Root objects whose renderers are the scenery's ground.</summary>
        private static readonly string[] kGroundRoots = { "Terrain" };

        private static readonly HashSet<string> s_Wanters = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        private static readonly List<Renderer> s_Hidden = new List<Renderer>();
        private static readonly List<Terrain> s_HiddenTerrain = new List<Terrain>();

        private struct CameraState
        {
            public Camera Cam;
            public CameraClearFlags Clear;
            public Color Background;
        }
        private static readonly List<CameraState> s_Cameras = new List<CameraState>();
        private static bool s_Applied;

        internal static bool Wants(string name) => s_Wanters.Contains(name);

        /// <summary>Ask for a black world on behalf of <paramref name="name"/>.</summary>
        internal static void Want(string name, StringBuilder log)
        {
            s_Wanters.Add(name);
            if (!s_Applied) Apply(log);
        }

        /// <summary>Withdraw <paramref name="name"/>'s request; the world comes back when nobody is left.</summary>
        internal static void Release(string name, StringBuilder log)
        {
            if (!s_Wanters.Remove(name)) return;
            if (s_Wanters.Count == 0 && s_Applied) Restore(log);
        }

        /// <summary>After a scene load, hide whatever new world objects arrived, if anyone still wants it.</summary>
        internal static void Reapply(StringBuilder log)
        {
            if (s_Wanters.Count == 0) return;
            Prune();
            Apply(log, summary: true);
        }

        /// <summary>Periodic pass: catch objects the game enabled since the last Apply. Logs only what it changes.</summary>
        internal static void Sweep(StringBuilder log)
        {
            if (s_Wanters.Count == 0) return;
            Prune();
            Apply(log, summary: false);
        }

        // Objects of an unloaded scene read as null; drop those and keep the rest, so
        // whatever is still hidden stays restorable.
        private static void Prune()
        {
            s_Hidden.RemoveAll(r => r == null);
            s_HiddenTerrain.RemoveAll(t => t == null);
            s_Cameras.RemoveAll(c => c.Cam == null);
        }

        private static void Apply(StringBuilder log, bool summary = true)
        {
            var hidden = 0;
            var scenes = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                scenes.Add(scene.name);
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (System.Array.IndexOf(kGroundRoots, root.name) < 0) continue;
                    // Inactive children included, so a child the game activates later is
                    // already off.
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        // Recorded already: keep it off, the game may have turned it back on.
                        if (s_Hidden.Contains(r)) { r.enabled = false; continue; }
                        if (!r.enabled) continue;
                        r.enabled = false;
                        s_Hidden.Add(r);
                        hidden++;
                        log?.AppendLine("blackout: hid " + Probe.FullPath(r.transform) + " (" + r.GetType().Name + ")");
                    }
                    foreach (var t in root.GetComponentsInChildren<Terrain>(true))
                    {
                        if (s_HiddenTerrain.Contains(t)) { t.enabled = false; continue; }
                        if (!t.enabled) continue;
                        t.enabled = false;
                        s_HiddenTerrain.Add(t);
                        hidden++;
                        log?.AppendLine("blackout: hid " + Probe.FullPath(t.transform) + " (Terrain)");
                    }
                }
            }

            var cams = 0;
            foreach (var cam in Camera.allCameras)
            {
                if (cam.clearFlags != CameraClearFlags.Skybox) continue;
                // Tracked but back on Skybox: the game reset it; the recorded original stands.
                if (IsTracked(cam)) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black; continue; }
                s_Cameras.Add(new CameraState { Cam = cam, Clear = cam.clearFlags, Background = cam.backgroundColor });
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cams++;
                log?.AppendLine("blackout: camera " + Probe.FullPath(cam.transform) + " skybox -> black");
            }

            var where = string.Join("+", scenes.ToArray());
            if (summary || hidden > 0 || cams > 0)
                log?.AppendLine("blackout: on in " + where + " - " + hidden + " renderer(s), " + cams
                                + " camera(s) newly hidden, " + (s_Hidden.Count + s_HiddenTerrain.Count) + "/" + s_Cameras.Count + " held");
            if (summary && s_Hidden.Count + s_HiddenTerrain.Count == 0)
                log?.AppendLine("blackout: no ground renderer found under " + string.Join("/", kGroundRoots)
                                + " in " + where + " - this scenery is not known here");
            // Last, so a throw above leaves the next Want free to try again.
            s_Applied = true;
        }

        private static bool IsTracked(Camera cam)
        {
            foreach (var c in s_Cameras)
                if (c.Cam == cam) return true;
            return false;
        }

        private static void Restore(StringBuilder log)
        {
            s_Applied = false;
            var restored = 0;
            foreach (var r in s_Hidden)
            {
                if (r == null) continue;   // the scene it lived in is gone
                r.enabled = true;
                restored++;
            }
            s_Hidden.Clear();
            foreach (var t in s_HiddenTerrain)
            {
                if (t == null) continue;
                t.enabled = true;
                restored++;
            }
            s_HiddenTerrain.Clear();

            var cams = 0;
            foreach (var c in s_Cameras)
            {
                if (c.Cam == null) continue;
                c.Cam.clearFlags = c.Clear;
                c.Cam.backgroundColor = c.Background;
                cams++;
            }
            s_Cameras.Clear();
            log?.AppendLine("blackout: off - " + restored + " renderer(s), " + cams + " camera(s) restored");
        }
    }
}
