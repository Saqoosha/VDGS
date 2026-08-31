using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VDGSCompanion
{
    /// <summary>
    /// The few things worth remembering between runs.
    ///
    /// Only one so far, and it earns its file: someone who keeps VelociDrone somewhere the
    /// search does not look had to point at it again every single time, which made the
    /// app feel like it was not paying attention.
    ///
    /// It lives beside the WebView's data rather than next to the exe, which may sit
    /// somewhere the user cannot write.
    /// </summary>
    internal sealed class Settings
    {
        public string Game { get; set; }

        /// <summary>
        /// Where the published captures are listed. Settable because the list is a plain
        /// file on a web server, and someone hosting their own should not need a new build.
        /// </summary>
        public string CatalogUrl { get; set; }

        private static string Path_ => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VDGSCompanion", "settings.json");

        internal static Settings Load()
        {
            try
            {
                if (File.Exists(Path_))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path_))
                           ?? new Settings();
            }
            catch { /* a corrupt file is not worth a dialog; the defaults are all usable */ }
            return new Settings();
        }

        internal void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_));
                File.WriteAllText(Path_,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8);
            }
            catch { /* not being able to remember is a nuisance, not a failure */ }
        }
    }
}
