using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

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
            var vdgs = Path.Combine(game, "vdgs");
            if (!Directory.Exists(vdgs)) yield break;
            foreach (var d in Directory.GetDirectories(vdgs))
                if (File.Exists(Path.Combine(d, "meta.json")))
                    yield return Path.GetFileName(d);
        }

        internal sealed class SceneInfo
        {
            public string Name;
            public long Splats;
            public bool Collision;   // without this the capture is flown straight through
            public long Bytes;
        }

        internal static List<SceneInfo> SceneDetails(string game)
        {
            var found = new List<SceneInfo>();
            foreach (var name in InstalledScenes(game))
            {
                var dir = Path.Combine(game, "vdgs", name);
                found.Add(new SceneInfo
                {
                    Name = name,
                    Splats = Json.SplatCount(Path.Combine(dir, "meta.json")),
                    Collision = File.Exists(Path.Combine(dir, "collision.bin")),
                    Bytes = DirectorySize(dir),
                });
            }
            return found;
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

                    // placement.json is where the player put the capture, and bindings.json
                    // is which track shows it. Both are theirs; an update must not undo it.
                    var leaf = Path.GetFileName(target);
                    if (File.Exists(target) &&
                        (leaf.Equals("placement.json", StringComparison.OrdinalIgnoreCase) ||
                         leaf.Equals("bindings.json", StringComparison.OrdinalIgnoreCase)))
                    {
                        log("kept your " + leaf);
                        continue;
                    }

                    e.ExtractToFile(target, overwrite: true);
                }
            }
            log("installed " + Path.GetFileName(zipPath));
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
