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
            // A capture has no sky and no floor beyond what was photographed, so the
            // game's terrain and horizon show through every hole. See SplatBackdrop.
            public bool backdrop = false;

            // On by default: a capture that has a collision mesh generated for it is meant
            // to be flown as a solid room, and files written before this field existed
            // keep the initialiser (JsonUtility only overwrites keys the file contains).
            // Scenes with no collision.bin ignore it.
            public bool collision = true;

            // What the collider LOOKS like: off / solid / wire. See SplatCollisionView.
            // Off by default - it is a diagnostic, not part of flying.
            public string collisionView = "off";
        }

        private readonly SplatMetaInfo m_Meta;

        /// <summary>True when this scene is a .ply read at load time, not a converted directory.</summary>
        /// <summary>Placement sits inside a converted directory, or beside a .ply.</summary>
        private string PlacementPath => IsPly
            ? Path.ChangeExtension(m_Dir, ".placement.json")
            : Path.Combine(m_Dir, "placement.json");

        private bool IsPly => m_Dir.EndsWith(".ply", StringComparison.OrdinalIgnoreCase);

        internal SplatScene(string path)
        {
            m_Dir = path;
            Name = IsPly
                ? Path.GetFileNameWithoutExtension(path)
                : new DirectoryInfo(path).Name;
            m_Meta = SplatMetaFile.Read(path);
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
                var name = new DirectoryInfo(dir).Name;
                if (VdgsPaths.IsReservedSceneName(name))
                {
                    report.AppendLine("skipping reserved dir: " + name);
                    continue;
                }
                if (!File.Exists(Path.Combine(dir, "meta.json")))
                    continue;
                found.Add(new SplatScene(dir));
                report.AppendLine("found splat scene: " + name);
            }

            // A .ply dropped straight in is converted at spawn time by PlyLoader. This is
            // the path that makes the mod distributable: producing a converted directory
            // needs a Python script and a second Unity install, which nobody but the
            // author has.
            foreach (var ply in Directory.GetFiles(vdgsDir, "*.ply"))
            {
                var name = Path.GetFileNameWithoutExtension(ply);
                if (found.Exists(s2 => string.Equals(s2.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    // A converted directory of the same name wins: it is already packed,
                    // so loading the .ply again would only be slower.
                    report.AppendLine("skipping " + Path.GetFileName(ply) + ": a converted scene of that name exists");
                    continue;
                }
                found.Add(new SplatScene(ply));
                report.AppendLine("found splat ply: " + Path.GetFileName(ply));
            }

            if (found.Count == 0)
                report.AppendLine("no splat scenes under " + vdgsDir + " (need <name>/meta.json or <name>.ply)");
            return found;
        }

        internal bool Spawn(StringBuilder report)
        {
            if (m_Go != null)
            {
                report.AppendLine(Name + ": already spawned");
                return true;
            }

            var data = IsPly ? PlyLoader.Load(m_Dir, out var error)
                             : SplatData.Load(m_Dir, out error);
            if (data == null)
            {
                report.AppendLine(Name + ": load failed - " + error);
                return false;
            }
            report.AppendLine(Name + ": " + data.Describe());

            m_Go = new GameObject("VDGS_" + Name);
            UnityEngine.Object.DontDestroyOnLoad(m_Go);

            var placement = LoadPlacement();
            m_Go.transform.position = new Vector3(placement.position[0], placement.position[1], placement.position[2]);
            m_Go.transform.eulerAngles = new Vector3(placement.rotation[0], placement.rotation[1], placement.rotation[2]);
            m_Go.transform.localScale = Vector3.one * placement.scale;

            m_Renderer = m_Go.AddComponent<SplatRenderer>();
            m_Renderer.SetData(data);

            if (placement.backdrop)
                SplatBackdrop.Attach(m_Go.transform, data.BoundsMin, data.BoundsMax,
                                     kBackdropMargin, kBackdropGroundY, report);

            // Off means nothing is built. Attaching regardless and disabling afterwards
            // would still cook the mesh - 277,736 triangles for drjohnson - inside the
            // spawn stall, so a perf run with collision switched off would silently
            // measure the collision work anyway. Turning it on later pays the cook once
            // and every toggle after that is free.
            if (placement.collision)
            {
                // Not drawn here: the cook runs on a worker thread and sharedMesh is null for
                // the first frames, so a view applied now would silently do nothing. Gated on
                // the attach succeeding - a pending view with no collider to draw on retries
                // once a second forever and writes a line to the track log each time.
                if (SplatCollision.Attach(m_Go.transform, m_Dir, report)
                    && placement.collisionView != SplatCollisionView.kOff)
                    m_PendingView = placement.collisionView;
            }
            else if (SplatCollision.Exists(m_Dir))
            {
                report.AppendLine("  collision off - mesh not loaded");
            }

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
            m_PendingView = null;
        }

        internal Transform Transform => m_Go != null ? m_Go.transform : null;
        internal int SplatCount => m_Renderer != null ? m_Renderer.SplatCount : m_Meta.Splats;
        internal string Source => "local";
        internal string Kind => m_Meta.Kind;
        internal string PosFormat => m_Meta.PosFormat;
        internal string ScaleFormat => m_Meta.ScaleFormat;
        internal string ColorFormat => m_Meta.ColorFormat;
        internal string ShFormat => m_Meta.ShFormat;
        internal long Bytes => m_Meta.Bytes;

        /// <summary>Current uniform scale, from the live object or from placement.json.</summary>
        internal float Scale => m_Go != null ? m_Go.transform.localScale.x : LoadPlacement().scale;

        /// <summary>Current height offset.</summary>
        internal float YOffset => m_Go != null ? m_Go.transform.position.y : LoadPlacement().position[1];

        /// <summary>
        /// Resizes the capture and sets its height, then persists both.
        ///
        /// Rotation and horizontal position deliberately stay out of the mod - a capture
        /// should arrive already oriented. These two are different: how big a room should
        /// be is a judgement about flying it rather than about the data being correct,
        /// and changing the scale moves the floor, so height has to come with it.
        /// </summary>
        /// <summary>Extra room around the capture, so the box never clips a splat.</summary>
        private const float kBackdropMargin = 0.25f;

        /// <summary>
        /// World height for the box's floor. The game's ground plane sits at 0, so a
        /// backdrop resting exactly there z-fights with it and its bottom face disappears.
        /// </summary>
        private const float kBackdropGroundY = 0.01f;

        internal bool BackdropOn => m_Go != null
            ? SplatBackdrop.IsAttached(m_Go.transform)
            : LoadPlacement().backdrop;

        /// <summary>Turn the black enclosing box on or off, and remember the choice.</summary>
        internal void SetBackdrop(bool on, StringBuilder log)
        {
            var p = LoadPlacement();
            p.backdrop = on;
            SavePlacementData(p, log);

            if (m_Go == null)
            {
                log?.AppendLine(Name + ": backdrop " + (on ? "on" : "off") + " (applies when spawned)");
                return;
            }

            if (!on)
            {
                SplatBackdrop.Detach(m_Go.transform);
                log?.AppendLine(Name + ": backdrop off");
                return;
            }

            var data = m_Renderer != null ? m_Renderer.Data : null;
            if (data == null)
            {
                log?.AppendLine(Name + ": backdrop wanted but no data loaded");
                return;
            }
            SplatBackdrop.Attach(m_Go.transform, data.BoundsMin, data.BoundsMax,
                                 kBackdropMargin, kBackdropGroundY, log);
        }

        /// <summary>Requested view, applied once the collider's mesh finishes cooking.</summary>
        private string m_PendingView;

        /// <summary>
        /// Applies a view that was asked for before the mesh finished cooking.
        ///
        /// The plugin already polls once a second for track changes; this rides along rather
        /// than adding a coroutine, so a view survives the gap between spawn and bake. Only
        /// armed for that one transient case - see SetCollisionView.
        /// </summary>
        internal void PumpPendingView(StringBuilder log)
        {
            if (m_PendingView == null || m_Go == null) return;
            if (SplatCollisionView.SetMode(m_Go.transform, m_PendingView, log))
                m_PendingView = null;
        }

        internal string CollisionView => m_Go != null
            ? SplatCollisionView.ModeOn(m_Go.transform)
            : LoadPlacement().collisionView;

        /// <summary>Draws the collision mesh, or stops drawing it, and remembers the choice.</summary>
        internal void SetCollisionView(string mode, StringBuilder log)
        {
            if (!SplatCollisionView.IsMode(mode))
            {
                log?.AppendLine(Name + ": unknown collision view '" + mode + "'");
                return;
            }

            var off = mode == SplatCollisionView.kOff;

            if (m_Go == null)
            {
                // Same rule as below, for a capture that is not spawned: do not record a
                // view for something with no mesh to draw.
                if (off || HasCollision)
                {
                    Remember(mode, log);
                    log?.AppendLine(Name + ": collision view " + mode + " (applies when spawned)");
                }
                else
                {
                    log?.AppendLine(Name + ": no collision mesh for this capture");
                }
                return;
            }

            // Drawing a shell for a capture whose collision is switched off would be worse
            // than drawing nothing: it looks like the walls are there when they stop nothing.
            // Not remembered either - the file would claim a view nobody can see.
            // IsEnabled, not IsAttached: a collider that exists but is switched off is
            // exactly the "collision is off" case this guard is about, and IsAttached is
            // true for it.
            var live = SplatCollision.IsEnabled(m_Go.transform);
            if (!live && !off)
            {
                log?.AppendLine(Name + ": collision is off - switch it on to see the mesh");
                return;
            }

            Remember(mode, log);
            m_PendingView = null;          // whatever was queued, this supersedes it

            if (SplatCollisionView.SetMode(m_Go.transform, mode, log)) return;

            // Retry ONLY while the mesh is still cooking, and ask that question directly
            // rather than inferring it from "a collider exists". SetMode also fails for a
            // missing shader and for a wire buffer that would not build, and neither gets
            // better by waiting - armed for those, PumpPendingView runs every second for
            // the rest of the session and writes a line to the track log each time.
            // Turning the view OFF never needs a retry: nothing is drawn either way.
            if (!off && SplatCollision.IsBaking(m_Go.transform)) m_PendingView = mode;
        }

        /// <summary>True when a collision mesh was generated for this capture.</summary>
        internal bool HasCollision => SplatCollision.Exists(m_Dir);

        internal bool CollisionOn => m_Go != null
            ? SplatCollision.IsEnabled(m_Go.transform)
            : LoadPlacement().collision;

        /// <summary>
        /// Make the capture solid or fly-through, and remember the choice.
        ///
        /// Meant to be used mid-flight: the same wall with and without a collider, one
        /// keypress apart, is the only honest way to judge whether the shell sits where the
        /// wall looks like it is.
        /// </summary>
        internal void SetCollision(bool on, StringBuilder log)
        {
            var p = LoadPlacement();
            p.collision = on;
            SavePlacementData(p, log);

            if (m_Go == null)
            {
                log?.AppendLine(Name + ": collision " + (on ? "on" : "off") + " (applies when spawned)");
                return;
            }

            // Build it on first enable. Spawn skips the load when collision is off, so
            // there may be no collider yet; after this the toggle only flips `enabled`,
            // which is what makes flipping it mid-flight free.
            if (on) SplatCollision.Attach(m_Go.transform, m_Dir, log);

            if (!SplatCollision.SetEnabled(m_Go.transform, on))
            {
                log?.AppendLine(Name + ": no collision mesh - nothing to switch");
                return;
            }

            // Switching the collider off takes the drawing with it. Leaving the shell up
            // over walls that no longer stop anything is the exact picture SetCollisionView
            // refuses to create - visible walls, and the drone flies through them. The
            // queued view goes too, or it would be applied to the disabled collider a
            // second later by PumpPendingView.
            if (!on)
            {
                m_PendingView = null;
                SplatCollisionView.SetMode(m_Go.transform, SplatCollisionView.kOff, log);
            }
            log?.AppendLine(Name + ": collision " + (on ? "on" : "off"));
        }

        private void Remember(string mode, StringBuilder log)
        {
            var p = LoadPlacement();
            p.collisionView = mode;
            SavePlacementData(p, log);
        }

        internal void SetTransform(float? scale, float? yOffset, StringBuilder log)
        {
            var p = LoadPlacement();

            if (scale.HasValue)
                p.scale = Mathf.Clamp(scale.Value, 0.01f, 100f);
            if (yOffset.HasValue)
                p.position[1] = Mathf.Clamp(yOffset.Value, -1000f, 1000f);

            if (m_Go != null)
            {
                m_Go.transform.localScale = Vector3.one * p.scale;
                var pos = m_Go.transform.position;
                m_Go.transform.position = new Vector3(pos.x, p.position[1], pos.z);
            }

            if (m_Go != null && SplatBackdrop.IsAttached(m_Go.transform))
            {
                var data = m_Renderer != null ? m_Renderer.Data : null;
                if (data != null)
                    SplatBackdrop.Attach(m_Go.transform, data.BoundsMin, data.BoundsMax,
                                         kBackdropMargin, kBackdropGroundY, log);
            }

            SavePlacementData(p, log);
            log?.AppendLine(Name + ": scale=" + p.scale.ToString("0.###")
                            + " y=" + p.position[1].ToString("0.###"));
        }

        internal void SavePlacement()
        {
            if (m_Go == null)
                return;
            var tr = m_Go.transform;

            // Patch the file rather than build a fresh Placement, so a field the live
            // objects cannot report keeps its stored value. `collision` is exactly that:
            // IsEnabled is false both for "switched off" and for "no mesh generated", and
            // writing the second case would silently pin collision off for a capture that
            // later gets a mesh.
            var p = LoadPlacement();
            p.position = new[] { tr.position.x, tr.position.y, tr.position.z };
            p.rotation = new[] { tr.eulerAngles.x, tr.eulerAngles.y, tr.eulerAngles.z };
            p.scale = tr.localScale.x;
            p.backdrop = SplatBackdrop.IsAttached(tr);
            // IsAttached, not Exists: a collision.bin that is present but failed to load
            // leaves Exists true and IsEnabled false, which would overwrite a user's "on"
            // with off and pin it there. Only a live collider can report its own state.
            if (SplatCollision.IsAttached(tr))
            {
                p.collision = SplatCollision.IsEnabled(tr);
                p.collisionView = SplatCollisionView.ModeOn(tr);
            }
            SavePlacementData(p, null);
        }

        private void SavePlacementData(Placement p, StringBuilder log)
        {
            try { File.WriteAllText(PlacementPath, JsonUtility.ToJson(p, true)); }
            catch (Exception e)
            {
                log?.AppendLine("placement save failed: " + e.Message);
                VdgsPlugin.Log.LogError("placement save failed: " + e.Message);
            }
        }

        private Placement LoadPlacement()
        {
            var path = PlacementPath;
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
                if (!SplatCollisionView.IsMode(p.collisionView)) p.collisionView = SplatCollisionView.kOff;
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
