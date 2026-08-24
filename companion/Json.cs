using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VDGSCompanion
{
    /// <summary>The two JSON shapes this tool reads and writes, and nothing else.</summary>
    internal static class Json
    {
        private static readonly JsonWriterOptions Pretty = new JsonWriterOptions { Indented = true };

        /// <summary>
        /// vdgs/bindings.json: track name -> the captures shown on it. Parsed rather than
        /// overwritten, because it holds every binding the player has, not just ours.
        /// </summary>
        internal static Dictionary<string, List<string>> ParseBindings(string text)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(text)) return map;
            using (var doc = JsonDocument.Parse(text))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var scenes = new List<string>();
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        foreach (var v in p.Value.EnumerateArray())
                            if (v.ValueKind == JsonValueKind.String) scenes.Add(v.GetString());
                    map[p.Name] = scenes;
                }
            }
            return map;
        }

        internal static string WriteBindings(Dictionary<string, List<string>> map)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms, Pretty))
                {
                    w.WriteStartObject();
                    foreach (var kv in map)
                    {
                        w.WriteStartArray(kv.Key);
                        foreach (var s in kv.Value) w.WriteStringValue(s);
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        internal sealed class TrackFile
        {
            public string Name;
            public long SceneId;
            public long Type;
            public string Value;    // the gates/barriers JSON, stored verbatim in the row
        }

        /// <summary>
        /// A .track.json as exported from a VelociDrone database: the row's own fields plus
        /// the course itself, kept as the exact string the game stores so nothing is
        /// reformatted on the way through.
        /// </summary>
        internal static TrackFile ParseTrackFile(string text)
        {
            using (var doc = JsonDocument.Parse(text))
            {
                var r = doc.RootElement;
                string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
                long Num(string k, long fallback) => r.TryGetProperty(k, out var v)
                    && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : fallback;

                var t = new TrackFile
                {
                    Name = Str("name"),
                    SceneId = Num("scene_id", -1),
                    Type = Num("type", 0),
                    Value = Str("value"),
                };
                if (string.IsNullOrEmpty(t.Name) || string.IsNullOrEmpty(t.Value) || t.SceneId < 0)
                    throw new InvalidDataException(
                        "not a VDGS track file (needs name, scene_id and value)");

                // `value` goes into the database untouched, so it is checked here rather
                // than discovered as an unopenable track later.
                using (JsonDocument.Parse(t.Value)) { }
                return t;
            }
        }
    }
}
