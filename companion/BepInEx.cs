using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace VDGSCompanion
{
    /// <summary>
    /// The loader the mod runs on, fetched and installed.
    ///
    /// It is not ours and is not carried in the app: it comes from its own release, pinned
    /// to one version and one digest, so what lands is the file that was checked rather
    /// than whatever that URL serves today.
    ///
    /// Doing this here is the difference between "download BepInEx, unzip it into the
    /// right folder, then come back" and pressing a button. That instruction was the first
    /// step of every install and the one most likely to be got wrong - unzipped one level
    /// too deep, it produces a game that starts and does nothing.
    /// </summary>
    internal static class BepInEx
    {
        internal const string Version = "5.4.23.5";

        private static readonly Catalog.File_ Release = new Catalog.File_
        {
            Url = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/" +
                  "BepInEx_win_x64_5.4.23.5.zip",
            Bytes = 639118,
            Sha256 = "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4",
        };

        internal static void Install(string game, Action<string> log)
        {
            log("fetching BepInEx " + Version);
            var temp = Path.Combine(Path.GetTempPath(), "vdgs-download");
            var zip = Catalog.Download(Release, temp, null);
            try
            {
                using (var archive = ZipFile.OpenRead(zip))
                {
                    foreach (var e in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue;

                        var target = Path.GetFullPath(Path.Combine(game, e.FullName));
                        var root = Path.GetFullPath(game) + Path.DirectorySeparatorChar;
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(
                                "archive escapes the game folder: " + e.FullName);

                        // Anything already there is either a previous install of the same
                        // files or a config the player has since edited.
                        if (File.Exists(target) && IsConfig(target)) continue;

                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        e.ExtractToFile(target, overwrite: true);
                    }
                }
                log("installed BepInEx " + Version);
                WriteLoggingConfig(game, log);
            }
            finally
            {
                try { File.Delete(zip); } catch { }
            }
        }

        private static bool IsConfig(string path) =>
            Path.GetExtension(path).Equals(".cfg", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// BepInEx writes no config until the game has been run once, and its defaults are
        /// wrong for this game in a way that costs real disk.
        ///
        /// Under -force-d3d12 the game's own Auto Exposure throws every frame - a fault in
        /// the game, not the mod, and harmless to the picture. With Unity log listening on,
        /// that exception is copied into the BepInEx log until it reaches tens of megabytes;
        /// one session was measured at 64 MB. Turning listening off leaves the exceptions
        /// in Player.log where they belong.
        /// </summary>
        private static void WriteLoggingConfig(string game, Action<string> log)
        {
            var path = Path.Combine(game, "BepInEx", "config", "BepInEx.cfg");
            if (File.Exists(path)) return;   // theirs, once the game has run

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,
                "[Logging]\r\n" +
                "## Whether to write the game's own Unity log into BepInEx's.\r\n" +
                "UnityLogListening = false\r\n" +
                "\r\n" +
                "[Logging.Disk]\r\n" +
                "Enabled = true\r\n" +
                "LogLevel = Fatal, Error, Warning, Message, Info\r\n",
                Encoding.UTF8);
            log("wrote BepInEx.cfg: disk logging on, Unity log listening off");
        }
    }
}
