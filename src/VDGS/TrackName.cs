using System;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// Reads the name of the track the game currently has loaded.
    ///
    /// VelociDrone never exposes this: Assembly-CSharp is obfuscated, so the field
    /// holding it is called `nnpnlmbjocf` and that name will change whenever the game is
    /// rebuilt with a new obfuscation seed. Rather than betting on one mangled name, all
    /// known carriers are tried in order, and the search falls back to scanning every
    /// string field on the relevant class.
    ///
    /// Carriers, established by probing with two different tracks loaded (see research/):
    ///   InGameChangeTrack.glnoaiifnln on ChangeTrackDialog - follows the loaded track
    ///   TextMeshProUGUI 'TrackName'   in RaceInfo2         - flight HUD, same value
    ///
    /// EditorManager.nnpnlmbjocf is deliberately NOT used. It holds the last track opened
    /// in the *editor* and does not change when a different track is loaded to fly, so
    /// reading it would confidently return the wrong name. It was the first field found
    /// and it looked right until a second track was loaded and it failed to update.
    /// </summary>
    internal static class TrackName
    {
        // Ordered by how reliable each carrier proved to be.
        private static readonly string[][] kCarriers =
        {
            new[] { "InGameChangeTrack", "glnoaiifnln" },
        };

        /// <summary>
        /// UI labels naming the current track: { GameObject name, required path fragment }.
        /// A null fragment means the name alone is unambiguous.
        ///
        /// The path fragment is not optional decoration on the first entry. The Change
        /// Scenery modal contains TWO labels called "Track Name" - the value, under
        /// Current Track/Table Entry/Entry, and the column header, under
        /// Current Track/Header, whose text is the literal string "Track Name". Matching
        /// on the object name alone picks up whichever comes first.
        ///
        /// The track list elsewhere in that screen is a different trap: every row has a
        /// label holding a track name, but they are the user's whole track library, not
        /// the loaded one. Only "Current Track" means current.
        /// </summary>
        private static readonly string[][] kLabels =
        {
            new[] { "Track Name", "Current Track/Table Entry" },
            // The flight HUD. Authoritative while racing, but it survives into the
            // editor holding the last track flown, so it must not be consulted first.
            new[] { "TrackName", null },
        };

        private static string s_Last;

        /// <summary>Best-effort current track name, or null if nothing could be read.</summary>
        internal static string Current(StringBuilder log = null)
        {
            foreach (var carrier in kCarriers)
            {
                var v = ReadField(carrier[0], carrier[1], log);
                if (!string.IsNullOrEmpty(v))
                    return Remember(v, log, carrier[0] + "." + carrier[1]);
            }

            // The mangled field name changed: take any plausible string field off the
            // same classes instead of giving up.
            foreach (var carrier in kCarriers)
            {
                var v = ScanClassForName(carrier[0], log);
                if (!string.IsNullOrEmpty(v))
                    return Remember(v, log, carrier[0] + ".<scanned>");
            }

            // UI labels, in order. Reached when the obfuscated field is not in this
            // scene at all - which is what happens in the track editor, where the
            // fallback used to return whatever track was flown last.
            foreach (var label in kLabels)
            {
                var v = ReadLabel(label[0], label[1], log);
                if (!string.IsNullOrEmpty(v))
                    return Remember(v, log, "label " + label[0] +
                                    (label[1] == null ? "" : " under " + label[1]));
            }

            log?.AppendLine("track name: not found by any carrier");
            return null;
        }

        private static string Remember(string v, StringBuilder log, string via)
        {
            v = v.Trim();
            if (v != s_Last)
            {
                s_Last = v;
                log?.AppendLine("track name: '" + v + "' (via " + via + ")");
            }
            return v;
        }

        private static string ReadField(string typeName, string fieldName, StringBuilder log)
        {
            var type = FindType(typeName);
            if (type == null) return null;

            FieldInfo f;
            try { f = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
            catch { return null; }
            if (f == null || f.FieldType != typeof(string)) return null;

            foreach (var c in Resources.FindObjectsOfTypeAll(type))
            {
                if (c == null) continue;
                try
                {
                    var v = f.GetValue(c) as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Every string field on the class, filtered down to something that could be a
        /// track name. Used when the obfuscated field name no longer resolves.
        /// </summary>
        private static string ScanClassForName(string typeName, StringBuilder log)
        {
            var type = FindType(typeName);
            if (type == null) return null;

            FieldInfo[] fields;
            try { fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
            catch { return null; }

            foreach (var c in Resources.FindObjectsOfTypeAll(type))
            {
                if (c == null) continue;
                foreach (var f in fields)
                {
                    if (f.FieldType != typeof(string)) continue;
                    string v;
                    try { v = f.GetValue(c) as string; } catch { continue; }
                    if (PlausibleTrackName(v))
                    {
                        log?.AppendLine("track name: recovered '" + v + "' from " + typeName + "." + f.Name);
                        return v;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Read a TextMeshPro-ish label by GameObject name, optionally requiring a
        /// fragment of its hierarchy path. Inactive objects count: the Change Scenery
        /// modal is closed most of the time but still holds the current track's name.
        /// </summary>
        private static string ReadLabel(string goName, string pathContains, StringBuilder log)
        {
            foreach (var c in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (c == null || c.gameObject == null) continue;
                if (c.gameObject.name != goName) continue;

                var t = c.GetType();
                if (t.Name.IndexOf("Text", StringComparison.Ordinal) < 0) continue;
                if (pathContains != null && PathOf(c.transform).IndexOf(
                        pathContains, StringComparison.Ordinal) < 0) continue;

                try
                {
                    var p = t.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                    var v = p?.GetValue(c, null) as string;
                    // A column header repeats its own object name; that is never a track.
                    if (v != null && v.Trim() == goName) continue;
                    if (PlausibleTrackName(v)) return v;
                }
                catch { }
            }
            return null;
        }

        private static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent)
                sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        /// <summary>
        /// A track name is short, printable, and not a path, URL or SQL fragment. The
        /// obfuscator scattered those through the string pool, so they must be excluded.
        /// </summary>
        private static bool PlausibleTrackName(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            v = v.Trim();
            if (v.Length < 1 || v.Length > 64) return false;
            if (v.IndexOf('/') >= 0 || v.IndexOf('\\') >= 0) return false;
            if (v.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (v.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            foreach (var ch in v)
                if (char.IsControl(ch)) return false;
            return true;
        }

        private static Type FindType(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != "Assembly-CSharp") continue;
                try
                {
                    foreach (var t in a.GetTypes())
                        if (t.Name == name) return t;
                }
                catch { }
            }
            return null;
        }
    }
}
