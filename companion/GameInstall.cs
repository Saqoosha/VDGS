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

        /// <summary>
        /// The places VelociDrone is commonly unzipped to.
        ///
        /// There is no default to look up. VelociDrone ships as a zip with no installer:
        /// wherever Launcher.exe is extracted to is where it downloads the game, and
        /// PatchKit records that nowhere - %LOCALAPPDATA%\PatchKit holds a 32-byte
        /// sender_id and nothing else. So this is a list of guesses, and a guess that
        /// misses is ordinary rather than exceptional.
        ///
        /// The guesses are the locations VelociDrone's own guide recommends: C:\VelociDrone,
        /// a second drive, the desktop, documents. Program Files is deliberately absent -
        /// the guide tells people to stay out of it, because the launcher writes into its
        /// own folder and UAC stops it. Steam paths were here and are gone: the game is
        /// not distributed through Steam, so they could never have matched.
        ///
        /// Each is checked twice, once as given and once with the app subfolder the
        /// launcher creates beside itself.
        /// </summary>
        internal static string FindGame()
        {
            foreach (var root in CandidateRoots())
            {
                if (IsGameFolder(root)) return root;
                var app = Path.Combine(root, "app");
                if (IsGameFolder(app)) return app;
            }
            return null;
        }

        /// <summary>
        /// The guesses themselves, apart from the looking, so a test can read the list
        /// rather than infer it from whether a search happened to succeed.
        /// </summary>
        internal static List<string> CandidateRoots() =>
            NamedRoots().Concat(DriveRoots()).ToList();

        /// <summary>
        /// The guesses written down here, apart from the ones derived from whatever
        /// drives happen to be mounted. Kept separate so a test can hold this list to
        /// what the guide says without a volume's name deciding the outcome.
        /// </summary>
        internal static List<string> NamedRoots()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots = new List<string>
            {
                @"C:\VelociDrone",
                Path.Combine(home, "Desktop", "VelociDrone"),
                Path.Combine(home, "Documents", "VelociDrone"),
                Path.Combine(home, "Downloads", "VelociDrone"),
                Path.Combine(home, "Downloads", "Velocidrone Windows Launcher"),
            };
            return roots;
        }

        /// <summary>
        /// One guess per mounted drive. "Another drive" is one of the guide's own
        /// suggestions, and it does not say the drive has to be internal - an external
        /// disk is where a large sim often ends up.
        /// </summary>
        internal static List<string> DriveRoots() =>
            DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => Path.Combine(d.RootDirectory.FullName, "VelociDrone"))
                .ToList();

        /// <summary>
        /// Looks for velocidrone.exe across the disks, for when the known locations miss.
        ///
        /// PatchKit records the install path nowhere - not in the registry, not in an
        /// uninstall entry, not in its own %LOCALAPPDATA% folder, which holds one 32-byte
        /// id and nothing else (checked on a real install). So when the candidate list
        /// misses, the choice is a scan or asking the person to go and find it themselves.
        ///
        /// Bounded rather than exhaustive: the game is unpacked by a launcher or a store,
        /// and neither buries it deeply.
        /// </summary>
        internal static string ScanForGame(Action<string> log)
        {
            var roots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            // Every ready drive, matching the guesses above. Fixed-only meant a game on
            // an external disk was missed twice over: the guess did not name the drive
            // and the walk did not visit it.
            foreach (var d in DriveInfo.GetDrives())
                if (d.IsReady)
                    roots.Add(d.RootDirectory.FullName);

            return ScanRoots(roots, maxDepth: 5, log: log);
        }

        /// <summary>The walk itself, taking its roots so it can be exercised on a fixture.</summary>
        internal static string ScanRoots(IEnumerable<string> roots, int maxDepth, Action<string> log)
        {
            // Big, and none of them is where a game lands.
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Windows", "$Recycle.Bin", "System Volume Information", "ProgramData",
                "AppData", "node_modules", ".git", "WindowsApps",
            };

            foreach (var root in roots)
            {
                log("looking under " + root);
                var queue = new Queue<KeyValuePair<string, int>>();
                queue.Enqueue(new KeyValuePair<string, int>(root, 0));
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    try
                    {
                        if (File.Exists(Path.Combine(item.Key, "velocidrone.exe")))
                        {
                            log("found " + item.Key);
                            return item.Key;
                        }
                        if (item.Value >= maxDepth) continue;
                        foreach (var sub in Directory.GetDirectories(item.Key))
                        {
                            var name = Path.GetFileName(sub);
                            if (name.StartsWith(".", StringComparison.Ordinal) || skip.Contains(name))
                                continue;
                            // A junction can point back up the tree and make this walk forever.
                            if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0)
                                continue;
                            queue.Enqueue(new KeyValuePair<string, int>(sub, item.Value + 1));
                        }
                    }
                    catch { /* unreadable folders are not where the game is */ }
                }
            }
            log("velocidrone.exe is not on any fixed disk");
            return null;
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
        ///
        /// <paramref name="label"/> is what to call it in the log. A download arrives under
        /// a temporary name, and reporting that name tells the reader nothing about what
        /// they just installed.
        /// </summary>
        internal static void InstallArchive(string game, string zipPath, Action<string> log,
                                            string label = null)
        {
            if (IsRunning())
                throw new InvalidOperationException(
                    "VelociDrone is running. Close it first - files in use cannot be replaced.");

            // A mod archive carries the interface; a capture archive does not. Only the
            // first should sweep vdgs/ui, so the archive is asked what it holds.
            bool carriesMod;
            using (var probe = ZipFile.OpenRead(zipPath))
                carriesMod = probe.Entries.Any(e =>
                    e.FullName.Replace('\\', '/').StartsWith("vdgs/ui/", StringComparison.OrdinalIgnoreCase));
            if (carriesMod) ClearInterface(game, log);

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
            log("installed " + (label ?? Path.GetFileName(zipPath)));
            if (carriesMod) EnableAutoSpawn(game, log);
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

            ClearInterface(game, log);

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
            EnableAutoSpawn(game, log);
        }

        /// <summary>
        /// Replaces the interface rather than merging into it.
        ///
        /// Vite fingerprints every asset, so each build lands beside the last one instead
        /// of over it. Four installs left eighteen scripts in vdgs/ui/assets, seventeen of
        /// them dead - index.html only ever names the current pair, so the rest are weight
        /// nobody would notice.
        ///
        /// Only the mod paths call this. A capture install goes through the same extractor
        /// and has no business touching the interface.
        /// </summary>
        private static void ClearInterface(string game, Action<string> log)
        {
            var ui = Path.Combine(game, "vdgs", "ui");
            if (!Directory.Exists(ui)) return;
            try { Directory.Delete(ui, recursive: true); log("cleared the old interface"); }
            catch (Exception ex) { log("could not clear vdgs/ui: " + ex.Message); }
        }

        /// <summary>
        /// Turns automatic display on, which is a file existing and nothing else.
        ///
        /// Without it the mod loads, finds its shaders, reads the bindings - and shows
        /// nothing, silently, because putting a capture on screen is gated on this. It was
        /// only ever created by hand, so every install done through this app arrived
        /// unable to draw anything, with no error to go on.
        ///
        /// Not part of the payload: deleting it is how someone turns automatic display
        /// off, and a reinstall that put it back would take that choice away. So it is
        /// written when absent and left alone when present.
        /// </summary>
        private static void EnableAutoSpawn(string game, Action<string> log)
        {
            var flag = Path.Combine(game, "vdgs", "autospawn");
            if (File.Exists(flag)) { log("left your autospawn alone"); return; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(flag));
                File.WriteAllBytes(flag, new byte[0]);
                log("enabled automatic display (vdgs/autospawn)");
            }
            catch (Exception ex) { log("could not write vdgs/autospawn: " + ex.Message); }
        }

        /// <summary>
        /// Removes what the mod installed, and only that.
        ///
        /// Captures stay: they are gigabytes, they were downloaded or built by hand, and
        /// nothing about turning the mod off says they are unwanted. bindings.json and the
        /// placements stay for the same reason - reinstalling should land where it left.
        /// BepInEx stays because it is not ours and other mods may use it.
        /// </summary>
        internal static void UninstallMod(string game, Action<string> log)
        {
            if (IsRunning())
                throw new InvalidOperationException(
                    "VelociDrone is running. Close it first - files in use cannot be removed.");

            var removed = 0;
            foreach (var rel in new[]
                     {
                         Path.Combine("BepInEx", "plugins", "VDGS.dll"),
                         Path.Combine("vdgs", "vdgs-shaders"),
                     })
            {
                var path = Path.Combine(game, rel);
                if (!File.Exists(path)) continue;
                File.Delete(path);
                log("removed " + rel);
                removed++;
            }

            var ui = Path.Combine(game, "vdgs", "ui");
            if (Directory.Exists(ui))
            {
                Directory.Delete(ui, recursive: true);
                log("removed vdgs/ui");
                removed++;
            }

            log(removed == 0 ? "nothing to remove" : "the mod is off; captures kept");
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

        /// <summary>
        /// Drops a track's entry from vdgs/bindings.json, leaving every other binding and
        /// the capture itself alone.
        /// </summary>
        internal static bool Unbind(string game, string trackName)
        {
            var path = Path.Combine(game, "vdgs", "bindings.json");
            if (!File.Exists(path)) return false;

            var map = ReadBindings(game);
            if (!map.Remove(trackName)) return false;
            File.WriteAllText(path, Json.WriteBindings(map));
            return true;
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
