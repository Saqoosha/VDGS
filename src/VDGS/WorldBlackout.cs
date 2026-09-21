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
    /// Renderer and not the GameObject keeps the BoxCollider solid. The sky is the flight
    /// camera's clear mode (Skybox, drawing RenderSettings.skybox); `Sky Dome` is inactive
    /// in the scene, so switching the camera to a solid black clear is the whole sky.
    /// Fog is already off in that scene and is deliberately not touched here.
    ///
    /// This is <see cref="SplatBackdrop"/> turned inside out: the backdrop hides the world
    /// by boxing the capture in, which only works while the capture is upright and only
    /// within the box; this hides the world itself, so it holds for a rotated capture and
    /// out past the capture's bounds too.
    ///
    /// World state, not capture state: several captures can be up at once and the world
    /// has to stay dark while any of them asks for it, so wanters are counted by name and
    /// the change is applied once, on the first, and undone once, on the last. Scene
    /// objects are per scene - a restart reloads the scene and brings a fresh, visible
    /// Plane - so <see cref="Reapply"/> runs from sceneLoaded while anyone still wants it.
    /// </summary>
    internal static class WorldBlackout
    {
        /// <summary>Root objects whose renderers are the scenery's ground.</summary>
        private static readonly string[] kGroundRoots = { "Terrain" };

        private static readonly HashSet<string> s_Wanters = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        private static readonly List<Renderer> s_Hidden = new List<Renderer>();

        private struct CameraState
        {
            public Camera Cam;
            public CameraClearFlags Clear;
            public Color Background;
        }
        private static readonly List<CameraState> s_Cameras = new List<CameraState>();
        private static bool s_Applied;

        internal static bool IsOn => s_Applied;
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

        /// <summary>
        /// After a scene load, hide the new scene's world if anyone still wants it. The
        /// old scene's objects are gone (their references read as null), so this is a
        /// fresh Apply, not a re-enable.
        /// </summary>
        internal static void Reapply(StringBuilder log)
        {
            if (s_Wanters.Count == 0) return;
            s_Applied = false;
            s_Hidden.Clear();
            s_Cameras.Clear();
            Apply(log);
        }

        private static void Apply(StringBuilder log)
        {
            s_Applied = true;
            var scene = SceneManager.GetActiveScene();
            var hidden = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (System.Array.IndexOf(kGroundRoots, root.name) < 0) continue;
                // Inactive children included: an inactive Terrain that the game turns on
                // later (TerrainQuality is a root object here) would otherwise reappear.
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled) continue;
                    r.enabled = false;
                    s_Hidden.Add(r);
                    hidden++;
                    log?.AppendLine("blackout: hid " + PathOf(r.transform) + " (" + r.GetType().Name + ")");
                }
            }

            var cams = 0;
            foreach (var cam in Camera.allCameras)
            {
                if (cam.clearFlags != CameraClearFlags.Skybox) continue;
                s_Cameras.Add(new CameraState { Cam = cam, Clear = cam.clearFlags, Background = cam.backgroundColor });
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cams++;
                log?.AppendLine("blackout: camera " + PathOf(cam.transform) + " skybox -> black");
            }

            log?.AppendLine("blackout: on in " + scene.name + " - " + hidden + " renderer(s), " + cams + " camera(s)");
            if (hidden == 0)
                log?.AppendLine("blackout: no ground renderer found under " + string.Join("/", kGroundRoots)
                                + " in " + scene.name + " - this scenery is not known here");
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

        private static string PathOf(Transform t)
        {
            var path = t.name;
            while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
            return path;
        }
    }
}
