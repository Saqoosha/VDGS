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

        private void Load()
        {
            m_Map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!System.IO.File.Exists(m_Path)) return;
                var text = System.IO.File.ReadAllText(m_Path);
                if (string.IsNullOrEmpty(text)) return;

                var parsed = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(text);
                if (parsed == null) return;

                foreach (var kv in parsed)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    m_Map[kv.Key.Trim()] = kv.Value ?? new List<string>();
                }
            }
            catch (Exception ex)
            {
                VdgsPlugin.Log.LogError("bindings.json parse failed: " + ex.Message);
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
