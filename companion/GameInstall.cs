using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace VDGSCompanion
{
    /// <summary>
    /// Finds VelociDrone, puts the mod and captures in place, and starts the game.
    ///
    /// Launching is not a convenience here. The splat shaders need Direct3D 12 and the game
    /// ships targeting D3D11, so without -force-d3d12 the captures simply do not draw -
    /// and nothing says why, in the game or in any log a player would look at. Every report
    /// of "the mod does nothing" starts here.
    /// </summary>
    internal static class GameInstall
    {
        internal const string LaunchArgs = "-force-d3d12";

        /// <summary>Where the PatchKit launcher unpacks the game, and the usual alternatives.</summary>
        internal static string FindGame()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new List<string>
            {
                Path.Combine(home, "Downloads", "Velocidrone Windows Launcher", "app"),
                @"C:\Program Files (x86)\Steam\steamapps\common\VelociDrone",
                @"C:\Program Files\VelociDrone",
            };
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                candidates.Add(Path.Combine(drive.RootDirectory.FullName,
                                            "SteamLibrary", "steamapps", "common", "VelociDrone"));

            return candidates.FirstOrDefault(IsGameFolder);
        }

        internal static bool IsGameFolder(string dir) =>
            !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "velocidrone.exe"));

        internal static bool IsRunning() =>
            Process.GetProcessesByName("velocidrone").Length > 0;

        internal static bool HasBepInEx(string game) =>
            File.Exists(Path.Combine(game, "winhttp.dll")) &&
            Directory.Exists(Path.Combine(game, "BepInEx"));

        internal static string InstalledModVersion(string game)
        {
            var dll = Path.Combine(game, "BepInEx", "plugins", "VDGS.dll");
            if (!File.Exists(dll)) return null;
            try { return FileVersionInfo.GetVersionInfo(dll).FileVersion; }
            catch { return "unknown"; }
        }

        internal static IEnumerable<string> InstalledScenes(string game)
        {
            foreach (var s in SceneDetails(game)) yield return s.Name;
        }

        internal sealed class SceneInfo
        {
            public string Name;
            public long Splats;
            public bool Collision;   // without this the capture is flown straight through
            public long Bytes;
            public bool Converted;   // false: a .ply the plugin parses at load time
        }

        /// <summary>
        /// Every capture the plugin will find, in both the shapes it accepts: a converted
        /// directory holding meta.json, and a bare .ply dropped straight into vdgs/.
        ///
        /// A directory beats a .ply of the same name - which is the plugin's own rule, and
        /// listing both would report one capture twice.
        /// </summary>
        internal static List<SceneInfo> SceneDetails(string game)
        {
            var found = new List<SceneInfo>();
            var vdgs = Path.Combine(game, "vdgs");
            if (!Directory.Exists(vdgs)) return found;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in Directory.GetDirectories(vdgs))
            {
                if (!File.Exists(Path.Combine(dir, "meta.json"))) continue;
                var name = Path.GetFileName(dir);
                seen.Add(name);
                found.Add(new SceneInfo
                {
                    Name = name,
                    Splats = Json.SplatCount(Path.Combine(dir, "meta.json")),
                    Collision = File.Exists(Path.Combine(dir, "collision.bin")),
                    Bytes = DirectorySize(dir),
                    Converted = true,
                });
            }

            foreach (var ply in Directory.GetFiles(vdgs, "*.ply"))
            {
                var name = Path.GetFileNameWithoutExtension(ply);
                if (!seen.Add(name)) continue;
                // The collision mesh and placement sit beside a .ply rather than inside it.
                var beside = Path.Combine(vdgs, name);
                long bytes = 0;
                foreach (var ext in new[] { ".ply", ".collision.bin", ".placement.json" })
                    if (File.Exists(beside + ext)) bytes += new FileInfo(beside + ext).Length;
                found.Add(new SceneInfo
                {
                    Name = name,
                    Splats = PlyVertexCount(ply),
                    Collision = File.Exists(beside + ".collision.bin"),
                    Bytes = bytes,
                    Converted = false,
                });
            }

            found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return found;
        }

        /// <summary>
        /// The splat count out of a .ply header. Only the header is read - these files run
        /// to hundreds of megabytes, and the number is on the second or third line.
        /// </summary>
        private static long PlyVertexCount(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                using (var r = new StreamReader(fs, Encoding.ASCII))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        if (line.StartsWith("end_header", StringComparison.Ordinal)) break;
                        if (!line.StartsWith("element vertex ", StringComparison.Ordinal)) continue;
                        long n;
                        if (long.TryParse(line.Substring("element vertex ".Length).Trim(), out n))
                            return n;
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Top level only. A capture is a flat handful of .bin files, and walking the tree
        /// would cost a recursion for a number shown beside a name.
        /// </summary>
        private static long DirectorySize(string dir)
        {
            try
            {
                long total = 0;
                foreach (var f in Directory.GetFiles(dir)) total += new FileInfo(f).Length;
                return total;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Unpacks a release archive over the game folder. Both the mod and the scene
        /// archives are laid out to be extracted here, so this is the same operation.
        /// </summary>
        internal static void InstallArchive(string game, string zipPath, Action<string> log)
        {
            if (IsRunning())
                throw new InvalidOperationException(
                    "VelociDrone is running. Close it first - files in use cannot be replaced.");

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;             // directory entry

                    // An archive names its own destinations (BepInEx/..., vdgs/...), so a
                    // crafted entry could otherwise write anywhere on the disk. Every path
                    // has to land inside the game folder.
                    var target = Path.GetFullPath(Path.Combine(game, e.FullName));
                    var root = Path.GetFullPath(game) + Path.DirectorySeparatorChar;
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("archive escapes the game folder: "
                                                            + e.FullName);

                    // README.txt beside the archive root is for the person, not the game.
                    if (!e.FullName.Contains("/") && !e.FullName.Contains("\\")) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(target));

                    if (KeepExisting(target, log)) continue;

                    e.ExtractToFile(target, overwrite: true);
                }
            }
            log("installed " + Path.GetFileName(zipPath));
        }

        /// <summary>
        /// placement.json is where the player put the capture, and bindings.json is which
        /// track shows it. Both are theirs; installing over them would undo work the mod
        /// exists to let them do.
        /// </summary>
        private static bool KeepExisting(string target, Action<string> log)
        {
            if (!File.Exists(target)) return false;
            var leaf = Path.GetFileName(target);
            if (!leaf.Equals("placement.json", StringComparison.OrdinalIgnoreCase) &&
                !leaf.Equals("bindings.json", StringComparison.OrdinalIgnoreCase)) return false;
            log("kept your " + leaf);
            return true;
        }

        /// <summary>
        /// The mod this app carries: the same tree a release archive holds, laid out to be
        /// copied straight over the game folder.
        ///
        /// It travels inside the app because the app is how the mod is handed out. Asking
        /// someone to go and find a zip first is asking them to do the job the app is for.
        /// </summary>
        internal static string BundledModDir()
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mod");
            return File.Exists(Path.Combine(dir, "BepInEx", "plugins", "VDGS.dll")) ? dir : null;
        }

        internal static string BundledModVersion()
        {
            var dir = BundledModDir();
            if (dir == null) return null;
            try
            {
                return FileVersionInfo
                    .GetVersionInfo(Path.Combine(dir, "BepInEx", "plugins", "VDGS.dll"))
                    .FileVersion;
            }
            catch { return null; }
        }

        /// <summary>Copies the carried mod over the game folder.</summary>
        internal static void InstallBundledMod(string game, Action<string> log)
        {
            var src = BundledModDir();
            if (src == null)
                throw new InvalidOperationException(
                    "This build carries no mod payload. Install from a vdgs-mod zip instead.");
            if (IsRunning())
                throw new InvalidOperationException(
                    "VelociDrone is running. Close it first - files in use cannot be replaced.");

            var root = Path.GetFullPath(src) + Path.DirectorySeparatorChar;
            var copied = 0;
            foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                // README.txt sits at the top of the payload and is for a person reading the
                // zip, not for the game.
                var relative = Path.GetFullPath(file).Substring(root.Length);
                if (relative.IndexOf(Path.DirectorySeparatorChar) < 0) continue;

                var target = Path.Combine(game, relative);
                if (KeepExisting(target, log)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, overwrite: true);
                copied++;
            }
            log("installed mod " + (BundledModVersion() ?? "?") + " (" + copied + " files)");
        }

        /// <summary>
        /// Binds a track name to a capture in vdgs/bindings.json, merging rather than
        /// replacing: the file is a map of every track the player has set up.
        /// </summary>
        internal static void Bind(string game, string trackName, string sceneName)
        {
            var path = Path.Combine(game, "vdgs", "bindings.json");
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            if (File.Exists(path))
                map = Json.ParseBindings(File.ReadAllText(path));

            map[trackName] = new List<string> { sceneName };

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.WriteBindings(map));
        }

        /// <summary>
        /// vdgs/bindings.json: which capture each track shows. Missing or unreadable is a
        /// normal state - it means nothing has been bound yet - so it reads as empty.
        /// </summary>
        internal static Dictionary<string, List<string>> ReadBindings(string game)
        {
            var path = Path.Combine(game, "vdgs", "bindings.json");
            try
            {
                return File.Exists(path)
                    ? Json.ParseBindings(File.ReadAllText(path))
                    : new Dictionary<string, List<string>>(StringComparer.Ordinal);
            }
            catch { return new Dictionary<string, List<string>>(StringComparer.Ordinal); }
        }

        internal static Process Launch(string game)
        {
            var exe = Path.Combine(game, "velocidrone.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("velocidrone.exe not found", exe);
            return Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = LaunchArgs,
                WorkingDirectory = game,
                UseShellExecute = true,
            });
        }
    }
}
