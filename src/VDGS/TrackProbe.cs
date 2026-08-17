using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VDGS
{
    /// <summary>
    /// Finds where the game keeps the identity of the track that is currently loaded.
    ///
    /// Binding a splat scene to a track needs that identity at runtime, but
    /// Assembly-CSharp is obfuscated: class and member names are mangled and string
    /// constants are shuffled, so the decompiled source lies. The only reliable approach
    /// is to walk the live object graph.
    ///
    /// A first pass over every instance and static *string field* found nothing, which
    /// rules out the obvious "current track name" variable - the game most likely keys
    /// off tracks.id and only materialises the name for display. So this pass also reads
    /// UI text components and properties, where the name provably appears on screen.
    ///
    /// Text components are matched by type name via reflection rather than by referencing
    /// UnityEngine.UI / TextMeshPro, so the plugin needs no extra assembly references.
    /// </summary>
    internal static class TrackProbe
    {
        private const int kMaxReported = 600;

        internal static void Dump(string path, string needle)
        {
            var sb = new StringBuilder();
            sb.AppendLine("======== track probe @ " + DateTime.Now.ToString("HH:mm:ss") + " ========");
            sb.AppendLine("activeScene = " + SceneManager.GetActiveScene().name);
            sb.AppendLine("needle      = " + (string.IsNullOrEmpty(needle) ? "(none - dumping everything)" : needle));
            sb.AppendLine();

            try
            {
                DumpTextComponents(sb, needle);
                DumpStringMembers(sb, needle);
                DumpStringCollections(sb, needle);
            }
            catch (Exception e)
            {
                sb.AppendLine("EXCEPTION: " + e);
            }

            try { File.WriteAllText(path, sb.ToString()); }
            catch (Exception e) { VdgsPlugin.Log.LogError("track probe write failed: " + e.Message); }
        }

        /// <summary>Anything with a `text` property: UI.Text, TextMeshPro, InputField.</summary>
        private static void DumpTextComponents(StringBuilder sb, string needle)
        {
            sb.AppendLine("-- UI text components --");
            int reported = 0;

            foreach (var c in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (c == null || reported >= kMaxReported) continue;
                var type = c.GetType();
                var n = type.Name;
                if (n.IndexOf("Text", StringComparison.Ordinal) < 0 &&
                    n.IndexOf("InputField", StringComparison.Ordinal) < 0)
                    continue;

                string value = null;
                try
                {
                    var p = type.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                    if (p != null && p.PropertyType == typeof(string))
                        value = p.GetValue(c, null) as string;
                }
                catch { continue; }

                if (string.IsNullOrEmpty(value)) continue;
                if (!Matches(value, needle)) continue;

                sb.AppendLine(string.Format("  {0} '{1}' = \"{2}\"   path: {3}",
                    n, c.gameObject != null ? c.gameObject.name : "?", Trim(value), FullPath(c)));
                reported++;
            }
            sb.AppendLine("  reported: " + reported);
            sb.AppendLine();
        }

        /// <summary>Instance/static string fields and properties on game components.</summary>
        private static void DumpStringMembers(StringBuilder sb, string needle)
        {
            sb.AppendLine("-- string fields and properties on game components --");
            int reported = 0;
            var seen = new HashSet<string>();

            foreach (var c in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (c == null || reported >= kMaxReported) continue;
                var type = c.GetType();
                if (!IsGameAssembly(type)) continue;

                foreach (var f in Fields(type))
                {
                    if (f.FieldType != typeof(string)) continue;
                    string v;
                    try { v = f.GetValue(c) as string; } catch { continue; }
                    if (!Matches(v, needle)) continue;

                    var key = type.Name + "." + f.Name + "=" + v;
                    if (!seen.Add(key)) continue;
                    sb.AppendLine(string.Format("  field {0}.{1} = \"{2}\"   (go: {3})",
                        type.Name, f.Name, Trim(v), c.gameObject != null ? c.gameObject.name : "?"));
                    if (++reported >= kMaxReported) break;
                }

                foreach (var p in Properties(type))
                {
                    if (p.PropertyType != typeof(string) || !p.CanRead) continue;
                    if (p.GetIndexParameters().Length != 0) continue;
                    string v;
                    try { v = p.GetValue(c, null) as string; } catch { continue; }
                    if (!Matches(v, needle)) continue;

                    var key = type.Name + "::" + p.Name + "=" + v;
                    if (!seen.Add(key)) continue;
                    sb.AppendLine(string.Format("  prop  {0}.{1} = \"{2}\"   (go: {3})",
                        type.Name, p.Name, Trim(v), c.gameObject != null ? c.gameObject.name : "?"));
                    if (++reported >= kMaxReported) break;
                }
            }

            // Statics live outside the object graph entirely.
            var game = FindGameAssembly();
            if (game != null)
            {
                Type[] types;
                try { types = game.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }

                foreach (var t in types)
                {
                    if (t == null || reported >= kMaxReported) continue;
                    foreach (var f in StaticFields(t))
                    {
                        if (f.FieldType != typeof(string) || f.IsLiteral) continue;
                        string v;
                        try { v = f.GetValue(null) as string; } catch { continue; }
                        if (!Matches(v, needle)) continue;
                        sb.AppendLine(string.Format("  static {0}.{1} = \"{2}\"", t.Name, f.Name, Trim(v)));
                        if (++reported >= kMaxReported) break;
                    }
                }
            }

            sb.AppendLine("  reported: " + reported);
            sb.AppendLine();
        }

        /// <summary>
        /// String collections: a track list held as List&lt;string&gt; or string[] would
        /// never show up in a plain field scan.
        /// </summary>
        private static void DumpStringCollections(StringBuilder sb, string needle)
        {
            sb.AppendLine("-- string collections --");
            int reported = 0;

            foreach (var c in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (c == null || reported >= kMaxReported) continue;
                var type = c.GetType();
                if (!IsGameAssembly(type)) continue;

                foreach (var f in Fields(type))
                {
                    if (f.FieldType == typeof(string)) continue;
                    if (!typeof(IEnumerable).IsAssignableFrom(f.FieldType)) continue;

                    object raw;
                    try { raw = f.GetValue(c); } catch { continue; }
                    if (raw == null || raw is string) continue;

                    int idx = 0;
                    try
                    {
                        foreach (var item in (IEnumerable)raw)
                        {
                            if (idx++ > 400) break;
                            var v = item as string;
                            if (!Matches(v, needle)) continue;
                            sb.AppendLine(string.Format("  {0}.{1}[{2}] = \"{3}\"   (go: {4})",
                                type.Name, f.Name, idx - 1, Trim(v),
                                c.gameObject != null ? c.gameObject.name : "?"));
                            if (++reported >= kMaxReported) break;
                        }
                    }
                    catch { }
                    if (reported >= kMaxReported) break;
                }
            }
            sb.AppendLine("  reported: " + reported);
            sb.AppendLine();
        }

        private static bool Matches(string v, string needle)
        {
            if (string.IsNullOrEmpty(v)) return false;
            if (!string.IsNullOrEmpty(needle))
                return v.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

            // No needle: fall back to the loose "looks like a name" filter.
            if (v.Length < 2 || v.Length > 64) return false;
            if (v.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (v.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            foreach (var ch in v)
                if (char.IsControl(ch) || ch > 126) return false;
            foreach (var ch in v)
                if (char.IsLetter(ch)) return true;
            return false;
        }

        private static string Trim(string v) =>
            v.Length <= 80 ? v.Replace("\n", "\\n") : v.Substring(0, 80).Replace("\n", "\\n") + "...";

        private static bool IsGameAssembly(Type t)
        {
            var asm = t.Assembly.GetName().Name;
            return !asm.StartsWith("Unity") && !asm.StartsWith("System") && !asm.StartsWith("mscorlib");
        }

        private static Assembly FindGameAssembly()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "Assembly-CSharp") return a;
            return null;
        }

        private static FieldInfo[] Fields(Type t)
        {
            try { return t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
            catch { return new FieldInfo[0]; }
        }

        private static FieldInfo[] StaticFields(Type t)
        {
            try { return t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
            catch { return new FieldInfo[0]; }
        }

        private static PropertyInfo[] Properties(Type t)
        {
            try { return t.GetProperties(BindingFlags.Instance | BindingFlags.Public); }
            catch { return new PropertyInfo[0]; }
        }

        private static string FullPath(Component c)
        {
            if (c == null || c.transform == null) return "?";
            return Probe.FullPath(c.transform);
        }
    }
}
