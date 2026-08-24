using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace VDGSCompanion
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Publishing a track means getting it out of user11.db, and this is the only
            // thing on the machine that can already read that file. A separate exporter
            // would be a second copy of the schema to keep in step.
            if (args.Length > 0 && args[0] == "--export-track")
            {
                Environment.ExitCode = ExportTrack(args);
                return;
            }

            // Whoever publishes a catalog needs to know the app can read it before anyone
            // clicks Get, and the answer should not require a GUI to find out.
            if (args.Length > 0 && args[0] == "--check-catalog")
            {
                Environment.ExitCode = CheckCatalog(args);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static int CheckCatalog(string[] args)
        {
            AttachConsole(-1);
            var url = args.Length > 1 ? args[1] : Catalog.DefaultUrl;
            try
            {
                var entries = Catalog.Fetch(url);
                Console.WriteLine(url + ": " + entries.Count + " capture(s)");
                foreach (var e in entries)
                {
                    Console.WriteLine("  " + e.Id + "  " + e.Name +
                                      "  " + e.Splats.ToString("N0") + " splats" +
                                      "  " + (e.Bytes / 1048576) + " MB" +
                                      "  " + (e.Licence ?? "no licence"));
                    Console.WriteLine("      scene -> " + e.InstallAs + "  " + e.Scene.Url);
                    Console.WriteLine("      track -> " +
                                      (e.Track == null ? "none" : e.TrackName + "  " + e.Track.Url));
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("cannot read " + url + ": " + ex.Message);
                return 1;
            }
        }

        private static int ExportTrack(string[] args)
        {
            // A GUI app has no console of its own; attaching to the one that launched it is
            // what makes this usable from a shell at all.
            AttachConsole(-1);

            if (args.Length < 2)
            {
                Console.Error.WriteLine(
                    "usage: VDGS.exe --export-track \"<track name>\" [out.track.json]\n" +
                    "       VDGS.exe --export-track --list");
                return 2;
            }

            var db = TrackStore.DatabasePath();
            if (!File.Exists(db))
            {
                Console.Error.WriteLine("no track database at " + db);
                return 1;
            }

            if (args[1] == "--list")
            {
                foreach (var t in TrackStore.List(db))
                    Console.WriteLine((t.FromServer ? "[server] " : "[local]  ") + t.Name);
                return 0;
            }

            var track = TrackStore.Find(db, args[1]);
            if (track == null)
            {
                Console.Error.WriteLine("no track called \"" + args[1] + "\" - try --list");
                return 1;
            }
            if (track.FromServer)
            {
                // Its author put it on the official server; republishing it here would be
                // taking someone else's course and handing it out under our own catalog.
                Console.Error.WriteLine(
                    "\"" + track.Name + "\" came from the official track server. " +
                    "Only tracks built locally are ours to publish.");
                return 1;
            }

            var outPath = args.Length > 2
                ? args[2]
                : SafeFileName(track.Name) + ".track.json";

            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteString("name", track.Name);
                    w.WriteNumber("scene_id", track.SceneId);
                    w.WriteNumber("type", track.Type);
                    // Written back byte for byte: the game stores this string, and
                    // reformatting it would make an imported track differ from the original
                    // for no reason anyone could see.
                    w.WriteString("value", track.Value);
                    w.WriteEndObject();
                }
                File.WriteAllBytes(outPath, ms.ToArray());
            }

            Console.WriteLine("wrote " + outPath + " (" + new FileInfo(outPath).Length + " bytes)");
            return 0;
        }

        private static string SafeFileName(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0 ? c : '-');
            return sb.ToString();
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);
    }
}
