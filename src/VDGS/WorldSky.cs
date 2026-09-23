using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// Puts the capture's own sky behind it, in place of the game's.
    ///
    /// <see cref="WorldBlackout"/> hides the game's world by clearing every camera to
    /// black, which is right for the ground - a capture's ground ends where the
    /// photographs ended, and anything behind it is the wrong ground - but wrong for the
    /// sky, where black is not what an outdoor capture looked like. A photographed sky is
    /// infinitely far away, so every frame that saw a patch of it saw the same patch from
    /// the same angle: the whole sky is recoverable from the footage as one panorama, and
    /// tools/sky_pano.py does exactly that from the SfM poses.
    ///
    /// The swap is <see cref="RenderSettings.skybox"/>, not the cameras: the game's own
    /// path already draws that material wherever nothing covered a pixel, so a sky costs
    /// one texture fetch on the pixels that were going to be cleared anyway, and the
    /// cameras keep the clear mode they shipped with. Blackout then leaves the sky alone
    /// whenever a sky is up - see its Apply.
    ///
    /// The panorama is stored in the frame of the capture's FILE, so the material's
    /// transform is the capture object's worldToLocal - which is what makes a live tweak
    /// of `turn` carry the sky with it instead of leaving the sun behind - plus the one
    /// thing the transform does not hold: a .ply read with mirrorY is flipped in Y as it
    /// loads, so its splats sit in a mirror of the file's frame and the sky has to be
    /// looked up through the same mirror or it hangs upside down over them.
    ///
    /// World state like blackout's, and for the same reason: several captures can be up,
    /// so wanters are counted by name and the last one out puts the game's sky back. The
    /// texture is cached per file - a despawn/respawn cycle is one dictionary lookup, not
    /// a 6 MB decode.
    /// </summary>
    internal static class WorldSky
    {
        /// <summary>Sky beside a .ply capture: the .ply's name with this in place of ".ply".</summary>
        internal const string PlySuffix = ".sky.jpg";

        /// <summary>Sky inside a converted capture's folder.</summary>
        internal const string DirFileName = "sky.jpg";

        private sealed class Sky
        {
            public Material Material;
            public Transform Follow;   // the capture the panorama's frame belongs to
            public bool MirrorY;       // the capture was flipped in Y as it loaded
        }

        private static readonly Dictionary<string, Sky> s_Wanters =
            new Dictionary<string, Sky>(StringComparer.OrdinalIgnoreCase);
        // Keyed by path, size and write time: a sky.jpg replaced while the game runs is a
        // different file, and keying by path alone would keep drawing the old one.
        private static readonly Dictionary<string, Texture2D> s_Textures =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        private static Material s_Original;
        private static bool s_Held;
        private static Sky s_Current;

        /// <summary>Whether a capture's sky is currently standing in for the game's.</summary>
        internal static bool Active => s_Current != null;

        internal static bool Wants(string name) => s_Wanters.ContainsKey(name);

        /// <summary>
        /// Where a capture's sky file would be, whether or not it exists.
        ///
        /// A converted capture keeps its sky INSIDE its folder, because everything that
        /// moves a capture moves the folder: a release zips vdgs/&lt;name&gt;/, the companion
        /// unpacks an update beside it and renames it into place, and Remove deletes it.
        /// A sibling file would ride none of that - not shipped, not swapped on update, and
        /// left behind by Remove to reappear under the next capture of that name. A .ply
        /// has no folder, so its sky sits beside it the way its placement.json does.
        /// </summary>
        internal static string PathFor(string captureDir) =>
            captureDir.EndsWith(".ply", StringComparison.OrdinalIgnoreCase)
                ? captureDir.Substring(0, captureDir.Length - 4) + PlySuffix
                : Path.Combine(captureDir, DirFileName);

        /// <summary>
        /// Show <paramref name="name"/>'s sky, if it has one and the shader to draw it.
        /// Returns false with a reason in the log when it cannot, which is not an error:
        /// a capture with no sky file simply keeps the blackout's black.
        /// </summary>
        internal static bool Want(string name, string captureDir, Transform follow, bool mirrorY,
                                  StringBuilder log)
        {
            // Asked again for a capture that already has one (blackout switched on twice):
            // keep its material. Building another would leak the first, which nothing
            // else holds a reference to.
            if (s_Wanters.TryGetValue(name, out var existing))
            {
                existing.Follow = follow;
                existing.MirrorY = mirrorY;
                Apply(log);
                return true;
            }

            var path = PathFor(captureDir);
            if (!File.Exists(path))
            {
                log?.AppendLine("sky: " + name + " has none (" + Path.GetFileName(path) + " not found)");
                return false;
            }

            var shader = ShaderBundle.PanoSkyShader;
            if (shader == null || !shader.isSupported)
            {
                log?.AppendLine("sky: " + name + " has " + Path.GetFileName(path)
                                + " but the panorama shader is "
                                + (shader == null ? "missing from the bundle" : "unsupported here")
                                + " - rebake the shader bundle");
                return false;
            }

            var tex = Texture(path, log);
            if (tex == null) return false;

            var mat = new Material(shader);
            mat.SetTexture("_Pano", tex);
            s_Wanters[name] = new Sky { Material = mat, Follow = follow, MirrorY = mirrorY };
            Apply(log);
            return true;
        }

        /// <summary>Withdraw <paramref name="name"/>'s sky; the game's comes back when nobody is left.</summary>
        internal static void Release(string name, StringBuilder log)
        {
            if (!s_Wanters.TryGetValue(name, out var sky)) return;
            s_Wanters.Remove(name);
            // Out of RenderSettings before it is destroyed: once it is no longer a wanter's,
            // Apply would take a material still sitting there for the scene's own sky and
            // Restore would later hand back a destroyed one.
            if (s_Held && RenderSettings.skybox == sky.Material) RenderSettings.skybox = s_Original;
            if (s_Current == sky) s_Current = null;
            if (sky.Material != null) UnityEngine.Object.Destroy(sky.Material);
            if (s_Wanters.Count == 0) Restore(log);
            else Apply(log);
        }

        /// <summary>
        /// Re-install the sky after a scene load, and keep the material's transform in
        /// step with the capture it belongs to. Cheap enough for the one-second poll:
        /// a matrix write when nothing moved, and nothing at all when no sky is up.
        /// </summary>
        internal static void Sweep(StringBuilder log)
        {
            if (s_Wanters.Count == 0) return;
            // A capture despawned without telling us - its transform is gone.
            List<string> dead = null;
            foreach (var kv in s_Wanters)
                if (kv.Value.Follow == null) (dead ?? (dead = new List<string>())).Add(kv.Key);
            if (dead != null)
                foreach (var n in dead) Release(n, log);
            if (s_Wanters.Count == 0) return;

            // RenderSettings is per scene, so a scene load hands back the game's own sky.
            if (s_Current != null && RenderSettings.skybox != s_Current.Material)
                Apply(log);
            else
                Track();
        }

        private static void Apply(StringBuilder log)
        {
            Sky pick = null;
            foreach (var kv in s_Wanters) { pick = kv.Value; break; }
            if (pick == null) return;

            // Whatever is in RenderSettings now and is not one of ours is the scene's own
            // sky, and that is what Restore has to put back. Taking it once, on the first
            // Apply, is wrong after a scene load: RenderSettings is per scene, the new one
            // brings its own sky, and restoring the old scene's material hands back the
            // wrong sky - or a destroyed one.
            if (!IsOurs(RenderSettings.skybox))
            {
                s_Original = RenderSettings.skybox;
                s_Held = true;
            }
            var changed = s_Current != pick || RenderSettings.skybox != pick.Material;
            s_Current = pick;
            RenderSettings.skybox = pick.Material;
            Track();
            if (changed)
            {
                log?.AppendLine("sky: on (" + s_Wanters.Count + " capture(s) asking)");
                // Blackout decides what the cameras clear to by asking whether a sky is
                // up, and it only asks when it applies. Another capture's blackout may have
                // cleared them to black already; hand them back now rather than on the
                // next once-a-second sweep, which only runs in flyable scenes.
                WorldBlackout.Sweep(log);
            }
        }

        private static bool IsOurs(Material m)
        {
            if (m == null) return false;
            foreach (var kv in s_Wanters)
                if (kv.Value.Material == m) return true;
            return false;
        }

        private static readonly Matrix4x4 kFlipY = Matrix4x4.Scale(new Vector3(1, -1, 1));

        // The panorama is in the capture file's frame; the shader needs world -> that frame.
        private static void Track()
        {
            if (s_Current == null || s_Current.Follow == null) return;
            var m = s_Current.Follow.worldToLocalMatrix;
            if (s_Current.MirrorY) m = kFlipY * m;
            s_Current.Material.SetMatrix("_SkyToPano", m);
        }

        private static void Restore(StringBuilder log)
        {
            if (!s_Held) return;
            RenderSettings.skybox = s_Original;
            s_Original = null;
            s_Held = false;
            s_Current = null;
            log?.AppendLine("sky: off - the game's own is back");
            // Another capture may still want blackout, and with no sky up that means black.
            WorldBlackout.Sweep(log);
        }

        private static Texture2D Texture(string path, StringBuilder log)
        {
            string key;
            try
            {
                var info = new FileInfo(path);
                key = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception e)
            {
                log?.AppendLine("sky: cannot read " + path + " - " + e.Message);
                return null;
            }
            if (s_Textures.TryGetValue(key, out var cached) && cached != null) return cached;

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception e)
            {
                log?.AppendLine("sky: cannot read " + path + " - " + e.Message);
                return null;
            }

            // mipChain: the sky is smooth but the horizon is not, and a drone's roll
            // walks it across the screen; without mips that edge crawls.
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, true);
            if (!tex.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(tex);
                log?.AppendLine("sky: " + Path.GetFileName(path) + " is not an image Unity can read");
                return null;
            }
            // Wrap in x so the seam at the back of the panorama closes; clamp in y so the
            // top row does not bleed into the bottom one at the pole.
            tex.wrapModeU = TextureWrapMode.Repeat;
            tex.wrapModeV = TextureWrapMode.Clamp;
            // RGB24 is 3 bytes a pixel resident; DXT1 is half a byte and the sky has no
            // detail a block compressor can hurt. 2048x1024 goes 6 MB -> 1.4 MB with mips.
            tex.Compress(true);
            tex.Apply(true, true);
            s_Textures[key] = tex;
            log?.AppendLine("sky: loaded " + Path.GetFileName(path) + " " + tex.width + "x" + tex.height
                            + " " + tex.format);
            return tex;
        }
    }
}
