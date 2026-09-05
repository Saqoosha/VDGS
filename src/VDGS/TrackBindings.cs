using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace VDGS
{
    /// <summary>
    /// Which splat scenes belong to which track.
    ///
    /// Stored as &lt;game&gt;/vdgs/bindings.json so it survives plugin rebuilds and can be
    /// edited by hand:
    ///
    ///   {
    ///     "2026 Fusion Flight Festival - Presented by Neos": ["shibuya"],
    ///     "Split-S": ["luigi", "bonsai"]
    ///   }
    ///
    /// Newtonsoft, not Unity's JsonUtility: JsonUtility cannot serialise a dictionary at
    /// all, and it silently emits "{}" for nested types - no exception, no warning, just
    /// an empty file that looks like a successful write. The game ships Newtonsoft 13 in
    /// its Managed folder, so using it costs nothing.
    /// </summary>
    internal class TrackBindings
    {
        private readonly string m_Path;
        private Dictionary<string, List<string>> m_Map =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // The stamp of the last write this object knows about - either what it read or
        // what it wrote. Anything else on disk came from outside and wins.
        private DateTime m_Stamp = DateTime.MinValue;
        private long m_Length = -1;

        internal TrackBindings(string path)
        {
            m_Path = path;
            Load();
        }

        internal int Count => m_Map.Count;

        /// <summary>Splat scene names bound to a track; empty list when unbound.</summary>
        internal List<string> For(string track)
        {
            if (!string.IsNullOrEmpty(track) && m_Map.TryGetValue(track.Trim(), out var v))
                return v;
            return new List<string>();
        }

        internal bool Has(string track) =>
            !string.IsNullOrEmpty(track) && m_Map.ContainsKey(track.Trim());

        /// <summary>Binds a track to exactly this set of splats and writes the file.</summary>
        internal void Set(string track, IEnumerable<string> splats, StringBuilder log)
        {
            if (string.IsNullOrEmpty(track))
            {
                log?.AppendLine("cannot bind: no track name available");
                return;
            }

            var list = new List<string>();
            foreach (var s in splats)
                if (!string.IsNullOrEmpty(s) && !list.Contains(s))
                    list.Add(s);

            // Binding nothing means "this track shows no splats", which is expressed by
            // removing the entry rather than storing an empty list.
            if (list.Count == 0)
                m_Map.Remove(track.Trim());
            else
                m_Map[track.Trim()] = list;

            Save(log);
            log?.AppendLine("bound '" + track + "' -> [" + string.Join(", ", list.ToArray()) + "]");
        }

        /// <summary>
        /// Re-reads bindings.json when someone else has written it.
        ///
        /// The companion writes this file directly, with the game either running or not,
        /// so the copy held here goes stale without anything saying so - and the next
        /// Save() would put the stale copy back over the new one. Called once a second
        /// from the track poll, which is already running.
        ///
        /// Length is compared as well as time because two writes inside one filesystem
        /// timestamp tick are indistinguishable otherwise, and a binding edit is exactly
        /// the kind of small change that lands in the same tick as the one before it.
        /// </summary>
        internal void ReloadIfChanged()
        {
            try
            {
                if (!System.IO.File.Exists(m_Path))
                    return;
                var info = new System.IO.FileInfo(m_Path);
                if (info.LastWriteTimeUtc == m_Stamp && info.Length == m_Length)
                    return;
                // Load() already logged the parse failure when it returns false - saying
                // "reloaded" on top of that would read as confirmation the reload worked,
                // in exactly the log someone reaches for when a capture just vanished.
                if (Load())
                    VdgsPlugin.Log.LogInfo("[VDGS] bindings.json changed on disk, reloaded ("
                                           + m_Map.Count + " track(s))");
            }
            catch (Exception ex)
            {
                VdgsPlugin.Log.LogError("bindings.json reload failed: " + ex.Message);
            }
        }

        private void Stamp()
        {
            try
            {
                var info = new System.IO.FileInfo(m_Path);
                m_Stamp = info.LastWriteTimeUtc;
                m_Length = info.Length;
            }
            catch
            {
                m_Stamp = DateTime.MinValue;
                m_Length = -1;
            }
        }

        /// <summary>
        /// Parses bindings.json into m_Map. Returns whether it succeeded.
        ///
        /// Builds the replacement map locally and only swaps it in on success. The
        /// companion writes this file with a plain truncate-then-write, so a poll can
        /// genuinely catch it mid-write; if a parse failure blanked m_Map immediately,
        /// every currently-displayed capture would vanish for a file that is about to
        /// become valid again a moment later. Leaving m_Map untouched on failure means
        /// a bad read costs nothing beyond the log line - the last-known-good bindings
        /// keep working until a good read replaces them. The field initializer already
        /// gives m_Map an empty (never null) starting value, so the very first Load()
        /// from the constructor fails into that same safe, empty, non-null state.
        /// </summary>
        private bool Load()
        {
            try
            {
                if (!System.IO.File.Exists(m_Path))
                {
                    m_Map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    return true;
                }
                var text = System.IO.File.ReadAllText(m_Path);
                if (string.IsNullOrEmpty(text))
                {
                    m_Map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    return true;
                }

                var parsed = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(text);
                var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (parsed != null)
                {
                    foreach (var kv in parsed)
                    {
                        if (string.IsNullOrEmpty(kv.Key)) continue;
                        map[kv.Key.Trim()] = kv.Value ?? new List<string>();
                    }
                }
                m_Map = map;
                return true;
            }
            catch (Exception ex)
            {
                // Corrupt file, not "no bindings" - m_Map is deliberately left alone here.
                VdgsPlugin.Log.LogError("bindings.json parse failed: " + ex.Message);
                return false;
            }
            finally
            {
                // Every exit above (not-found, empty file, bad JSON, or a clean parse)
                // must land here - ReloadIfChanged compares against this stamp, and a
                // path that skipped it would report the file as "changed" forever. This
                // also means a parse failure is not retried every second: only a further
                // write moves the stamp again.
                Stamp();
            }
        }

        private void Save(StringBuilder log)
        {
            try
            {
                var json = JsonConvert.SerializeObject(m_Map, Formatting.Indented);

                // Never let a serialisation failure wipe real bindings.
                if (m_Map.Count > 0 && (string.IsNullOrEmpty(json) || json.Trim() == "{}"))
                {
                    log?.AppendLine("bindings.json NOT written: serialiser returned '" + json + "'");
                    VdgsPlugin.Log.LogError("bindings.json serialisation produced nothing - refusing to write");
                    return;
                }

                System.IO.File.WriteAllText(m_Path, json);
                Stamp();
                log?.AppendLine("bindings.json written (" + m_Map.Count + " track(s))");
            }
            catch (Exception ex)
            {
                log?.AppendLine("bindings.json write failed: " + ex.Message);
                VdgsPlugin.Log.LogError("bindings.json write failed: " + ex.Message);
            }
        }

        /// <summary>Drops a track's binding entirely. No-op when it was not bound.</summary>
        internal void Remove(string track, StringBuilder log)
        {
            if (string.IsNullOrEmpty(track)) return;
            if (!m_Map.Remove(track.Trim()))
            {
                log?.AppendLine("'" + track + "' was not bound");
                return;
            }
            Save(log);
            log?.AppendLine("unbound '" + track + "'");
        }

        /// <summary>Snapshot for the web UI.</summary>
        internal Dictionary<string, List<string>> All()
        {
            return new Dictionary<string, List<string>>(m_Map, StringComparer.OrdinalIgnoreCase);
        }

        internal string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("bindings (" + m_Map.Count + "):");
            foreach (var kv in m_Map)
                sb.AppendLine("  '" + kv.Key + "' -> [" + string.Join(", ", kv.Value.ToArray()) + "]");
            return sb.ToString();
        }
    }
}
