using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// Pins every game-view camera to a fixed pose read from &lt;game&gt;/vdgs/evalcam.json, so
    /// the same view can be rendered in-game, in the offline harness and in a web viewer
    /// and the three subtracted. Diagnostic only - delete the file to get the cameras back.
    ///
    ///   { "pos": [x,y,z], "fwd": [x,y,z], "up": [x,y,z], "fov": 47.83, "black": true }
    ///
    /// "record": { "poses": "path.json", "out": "rec", "warmup": 30 } plays a pose list
    /// one pose per rendered frame and saves every frame, so a whole flight comes out in
    /// the time the game takes to render it. Pose and screenshot land in the same frame
    /// (pose set in Update, applied in onPreCull, captured at the end of that frame), so
    /// the images cannot drift behind the poses the way an outside capture loop does.
    /// "poses" is a JSON array of { "pos", "fwd", "up", "name"? }; both paths are relative
    /// to &lt;game&gt;/vdgs. "warmup" frames hold the first pose before recording starts,
    /// for the sort and the spawn to settle. rec.done is written in "out" when finished.
    /// "fov" and the renderer knobs are the top-level keys and apply to every pose; the
    /// camera stays on the last pose afterwards, and "out" is not emptied first.
    ///
    /// "black" clears to solid black and sets cullingMask to 0. The splats survive that
    /// because they are drawn from a CommandBuffer rather than by the culling pass, so
    /// what is left on screen is splats over black - the exact condition a WebGL viewer
    /// renders, which is what makes a pixel-for-pixel subtraction meaningful.
    ///
    /// Applied in Camera.onPreCull, i.e. after the game's own camera scripts have moved the
    /// camera for the frame, so nothing has to be disabled to win.
    /// </summary>
    internal static class EvalCam
    {
        private static string s_Path;
        private static DateTime s_Mtime;
        private static float s_Timer;
        private static bool s_Active;
        private static Vector3 s_Pos, s_Fwd, s_Up;
        private static float s_Fov;
        private static bool s_Black;
        // Optional overrides for A/B-ing the renderer against a web viewer. -1 = leave alone.
        private static int s_ShOrder = -1;
        private static float s_GaussCut = -1f;
        private static int s_DropDegenerate = -1;
        private static float s_CullSlack = -1f;
        private static int s_DepthClip = -1;
        private static bool s_Pin;
        // Recording: null when not recording.
        private static JArray s_RecPoses;
        private static string s_RecOut;
        private static int s_RecIdx;

        internal static void Init(string gameRoot)
        {
            s_Path = Path.Combine(gameRoot, "vdgs", "evalcam.json");
            Camera.onPreCull += Apply;
        }

        /// <summary>Call once per frame; re-reads the file when it appears or changes.</summary>
        internal static void Poll(float dt)
        {
            if (s_Active && s_RecPoses != null) StepRecord();

            s_Timer += dt;
            if (s_Timer < 1f) return;
            s_Timer = 0f;

            try
            {
                if (!File.Exists(s_Path))
                {
                    if (s_Active) Probe.Write("evalcam: off");
                    s_Active = false;
                    return;
                }
                var mtime = File.GetLastWriteTimeUtc(s_Path);
                if (s_Active && mtime == s_Mtime)
                {
                    // Re-applied every second rather than once at parse time: a capture
                    // spawns after the file is read, and would otherwise keep full SH.
                    ApplyOverrides();
                    return;
                }
                s_Mtime = mtime;

                var j = JObject.Parse(File.ReadAllText(s_Path));
                // Pose is optional: a file with only renderer knobs leaves the game
                // camera alone, which is how a normal flight view gets A/B-ed.
                s_Pin = j["pos"] != null;
                if (s_Pin)
                {
                    s_Pos = Vec(j["pos"]);
                    s_Fwd = Vec(j["fwd"]).normalized;
                    s_Up = Vec(j["up"]).normalized;
                }
                s_Fov = j["fov"] != null ? (float)j["fov"] : 60f;
                s_Black = j["black"] != null && (bool)j["black"];
                // "shOrder": 0 renders DC only, which is what the web reference viewers do.
                // Any difference it makes is spherical-harmonic colour, not geometry.
                s_ShOrder = j["shOrder"] != null ? (int)j["shOrder"] : -1;
                // "gaussCut": 4 matches the web viewers' two-sigma discard; "dropDegenerate"
                // matches their `if (lambda2 < 0.0) return;`.
                s_GaussCut = j["gaussCut"] != null ? (float)j["gaussCut"] : -1f;
                s_DropDegenerate = j["dropDegenerate"] != null ? ((bool)j["dropDegenerate"] ? 1 : 0) : -1;
                s_CullSlack = j["cullCenterSlack"] != null ? (float)j["cullCenterSlack"] : -1f;
                s_DepthClip = j["depthClip"] != null ? ((bool)j["depthClip"] ? 1 : 0) : -1;
                s_RecPoses = null;
                var rec = j["record"];
                if (rec != null)
                {
                    var dir = Path.GetDirectoryName(s_Path);
                    s_RecPoses = JArray.Parse(File.ReadAllText(Path.Combine(dir, (string)rec["poses"])));
                    s_RecOut = Path.Combine(dir, rec["out"] != null ? (string)rec["out"] : "rec");
                    Directory.CreateDirectory(s_RecOut);
                    File.Delete(Path.Combine(s_RecOut, "rec.done"));
                    s_RecIdx = -(rec["warmup"] != null ? (int)rec["warmup"] : 30);
                    s_Pin = true;
                    SetPose((JObject)s_RecPoses[0]);
                    Probe.Write("evalcam: recording " + s_RecPoses.Count + " poses -> " + s_RecOut);
                }
                s_Active = true;
                ApplyOverrides();
                Probe.Write("evalcam: on pos=" + s_Pos + " fwd=" + s_Fwd + " up=" + s_Up
                            + " fov=" + s_Fov + " black=" + s_Black + " shOrder=" + s_ShOrder
                            + " gaussCut=" + s_GaussCut + " dropDegenerate=" + s_DropDegenerate
                            + " cullSlack=" + s_CullSlack);
            }
            catch (Exception e)
            {
                s_Active = false;
                Probe.Write("evalcam: bad file - " + e.Message);
            }
        }

        private static void ApplyOverrides()
        {
            if (s_ShOrder < 0 && s_GaussCut < 0f && s_DropDegenerate < 0 && s_CullSlack < 0f
                && s_DepthClip < 0) return;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<SplatRenderer>())
            {
                if (r == null) continue;
                if (s_ShOrder >= 0) r.m_SHOrder = s_ShOrder;
                if (s_GaussCut >= 0f) r.m_GaussCut = s_GaussCut;
                if (s_DropDegenerate >= 0) r.m_DropDegenerate = s_DropDegenerate != 0;
                if (s_CullSlack >= 0f) r.m_CullCenterSlack = s_CullSlack;
                if (s_DepthClip >= 0) r.m_DepthClip = s_DepthClip != 0;
            }
        }

        private static void SetPose(JObject p)
        {
            s_Pos = Vec(p["pos"]);
            s_Fwd = Vec(p["fwd"]).normalized;
            s_Up = Vec(p["up"]).normalized;
        }

        /// <summary>One pose per frame: set it for this frame's render and capture that render.</summary>
        private static void StepRecord()
        {
            // Runs ahead of Poll's try, from Plugin.Update: a throw here would stop the
            // rest of Update every frame, so a bad pose ends the recording instead.
            try { StepRecordUnguarded(); }
            catch (Exception e)
            {
                Probe.Write("evalcam: recording stopped at pose " + Math.Max(s_RecIdx, 0) + " - " + e.Message);
                s_RecPoses = null;
            }
        }

        private static void StepRecordUnguarded()
        {
            int i = Math.Max(s_RecIdx, 0);
            if (i >= s_RecPoses.Count)
            {
                File.WriteAllText(Path.Combine(s_RecOut, "rec.done"), s_RecPoses.Count.ToString());
                Probe.Write("evalcam: recorded " + s_RecPoses.Count + " frames");
                s_RecPoses = null;
                return;
            }
            var p = (JObject)s_RecPoses[i];
            SetPose(p);
            if (s_RecIdx >= 0)
            {
                string name = p["name"] != null ? (string)p["name"] : i.ToString("D5");
                ScreenCapture.CaptureScreenshot(Path.Combine(s_RecOut, name + ".png"));
            }
            s_RecIdx++;
        }

        private static Vector3 Vec(JToken t)
        {
            return new Vector3((float)t[0], (float)t[1], (float)t[2]);
        }

        private static void Apply(Camera cam)
        {
            if (!s_Active || cam == null) return;
            // Only cameras that draw to the screen - not UI model cameras or reflections.
            if (cam.cameraType != CameraType.Game || cam.targetTexture != null) return;
            if (s_Pin)
            {
                cam.transform.position = s_Pos;
                cam.transform.rotation = Quaternion.LookRotation(s_Fwd, s_Up);
                cam.fieldOfView = s_Fov;
            }
            if (s_Black)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.cullingMask = 0;
                // allowHDR is deliberately left alone: turning it off changes the render
                // path being measured (and upstream has a separate upside-down bug there).

                // PostProcessing keeps running with an empty culling mask, and its bloom
                // spreads the bright lawn into the sky as a halo that decays with distance
                // from the horizon - which reads exactly like the "fog" being hunted. A web
                // viewer has no post stack at all, so it has to come off for the two images
                // to be comparable.
                var layer = cam.GetComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>();
                if (layer != null && layer.enabled) layer.enabled = false;
            }
        }
    }
}
