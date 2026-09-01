using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace VDGSCompanion
{
    /// <summary>
    /// The published list of captures, and fetching one.
    ///
    /// Captures are hundreds of megabytes and are not in the mod archive, so this is how
    /// someone gets one without being handed a file. What arrives is a zip and a track
    /// definition, installed by the same code an "Install capture" click uses - the
    /// download is a different way of obtaining the file, not a different way of using it.
    ///
    /// Everything fetched here is remote content landing on someone's disk, so: the
    /// transport must be TLS, the digest is checked before anything is unpacked, and the
    /// entry decides only what to download - never where it goes.
    /// </summary>
    internal static class Catalog
    {
        internal const string DefaultUrl = "https://vdgs.saqoo.sh/catalog.json";

        internal sealed class File_
        {
            public string Url;
            public long Bytes;
            public string Sha256;
        }

        internal sealed class Entry
        {
            public string Id;
            public string Name;
            public string Description;
            public string Author;
            public string Licence;
            public long Splats;
            public string MinModVersion;

            public File_ Scene;
            public string InstallAs;      // the folder name under vdgs/

            public File_ Track;
            public string TrackName;

            /// <summary>Total bytes to fetch, for a size shown before a click.</summary>
            public long Bytes => (Scene != null ? Scene.Bytes : 0) + (Track != null ? Track.Bytes : 0);

            /// <summary>
            /// Whether the track half of this entry is really in place: the course in the
            /// game's own database, and a binding aiming it at the capture.
            ///
            /// A capture sitting in a folder is not an install - nothing in the game
            /// reaches it. Reporting one as installed is what turned a failed track import
            /// into a dead end: the page greyed out the only button that could have
            /// finished the job, and running the game did not bring it back, because what
            /// disabled it never looked at the database.
            ///
            /// Not knowing counts as not done. When the database cannot be read there is
            /// no honest way to call this complete, and leaving Get alive costs a click,
            /// where claiming completion costs the only route to a working install.
            /// </summary>
            /// <param name="inGame">Track names the game knows, or null if unreadable.</param>
            /// <param name="bound">vdgs/bindings.json, keyed by track name.</param>
            internal bool TrackInPlace(Dictionary<string, bool> inGame,
                                       Dictionary<string, List<string>> bound)
            {
                // Nothing published to import: the capture on its own is the whole entry,
                // and whoever wants it bound does that themselves once flying.
                if (Track == null || TrackName == null) return true;

                // Ordinal throughout, like the bindings file and the mod that reads it -
                // these are the game's own track names, matched the way the game matches
                // them.
                return inGame != null && inGame.ContainsKey(TrackName)
                    && bound != null && bound.ContainsKey(TrackName);
            }
        }

        // ------------------------------------------------------------------ fetching

        private static void UseModernTls()
        {
            // .NET Framework 4.8 still negotiates from whatever the process was left with,
            // and a host that has turned off the old protocols answers with a connection
            // closed rather than anything a message could explain.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        /// <summary>
        /// http is refused except on loopback, which is how the harness serves a fixture.
        /// Anything else would let a catalog be swapped in transit for one naming other
        /// files - and the app installs what the catalog names.
        /// </summary>
        internal static void RequireSafeUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                throw new InvalidDataException("not a URL: " + url);
            if (uri.Scheme == Uri.UriSchemeHttps) return;
            if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return;
            throw new InvalidDataException("refusing a non-https address: " + url);
        }

        internal static List<Entry> Parse(string json)
        {
            var found = new List<Entry>();
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;

                // A newer format is not something to guess at: a missing field would read
                // as "no track" and quietly install half of what was published.
                if (root.TryGetProperty("formatVersion", out var v) && v.GetInt32() != 1)
                    throw new InvalidDataException(
                        "this catalog is format " + v.GetInt32() + "; this app reads 1. Update it.");

                JsonElement scenes;
                if (!root.TryGetProperty("scenes", out scenes) ||
                    scenes.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("no scenes in the catalog");

                foreach (var e in scenes.EnumerateArray())
                {
                    var entry = new Entry
                    {
                        Id = Str(e, "id"),
                        Name = Str(e, "name"),
                        Description = Str(e, "description"),
                        Author = Str(e, "author"),
                        Licence = Str(e, "licence"),
                        Splats = Num(e, "splats"),
                        MinModVersion = Str(e, "minModVersion"),
                    };

                    JsonElement scene;
                    if (e.TryGetProperty("scene", out scene) && scene.ValueKind == JsonValueKind.Object)
                    {
                        entry.Scene = ReadFile(scene);
                        entry.InstallAs = Str(scene, "installAs");
                    }

                    JsonElement track;
                    if (e.TryGetProperty("track", out track) && track.ValueKind == JsonValueKind.Object)
                    {
                        entry.Track = ReadFile(track);
                        entry.TrackName = Str(track, "name");
                    }

                    // An entry naming nothing to fetch is a row that cannot be acted on.
                    if (entry.Id == null || entry.Name == null || entry.Scene == null) continue;
                    found.Add(entry);
                }
            }
            return found;
        }

        private static File_ ReadFile(JsonElement e) => new File_
        {
            Url = Str(e, "url"),
            Bytes = Num(e, "bytes"),
            Sha256 = Str(e, "sha256"),
        };

        private static string Str(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static long Num(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

        internal static List<Entry> Fetch(string url)
        {
            RequireSafeUrl(url);
            UseModernTls();
            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", "VDGSCompanion");
                return Parse(client.DownloadString(url));
            }
        }

        // ------------------------------------------------------------------ downloading

        /// <summary>
        /// Fetches one file and checks it before handing it back.
        ///
        /// The digest is the point: the caller unpacks this over the game folder, so a
        /// truncated download or a swapped file has to fail here rather than there. It is
        /// written to a temporary name and only that name is returned - nothing is left
        /// where a failed download could be mistaken for a good one.
        /// </summary>
        internal static string Download(File_ file, string intoDir, Action<int> percent)
        {
            RequireSafeUrl(file.Url);
            UseModernTls();
            if (string.IsNullOrEmpty(file.Sha256))
                throw new InvalidDataException("the catalog gives no digest for " + file.Url);

            Directory.CreateDirectory(intoDir);
            var temp = Path.Combine(intoDir, "vdgs-" + Guid.NewGuid().ToString("N") + ".part");

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(file.Url);
                request.UserAgent = "VDGSCompanion";
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var source = response.GetResponseStream())
                using (var sink = new FileStream(temp, FileMode.Create, FileAccess.Write))
                {
                    var total = response.ContentLength > 0 ? response.ContentLength : file.Bytes;
                    var buffer = new byte[81920];
                    long done = 0;
                    var lastReported = -1;
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        sink.Write(buffer, 0, read);
                        done += read;
                        if (total <= 0 || percent == null) continue;
                        var p = (int)(done * 100 / total);
                        // Only on change: a report per 80 KB chunk of a 300 MB file is
                        // four thousand messages to the page for a hundred visible states.
                        if (p == lastReported) continue;
                        lastReported = p;
                        percent(p);
                    }
                }

                var actual = Sha256(temp);
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "the download does not match the catalog's digest - it was truncated or " +
                        "is not the file that was published");
                return temp;
            }
            catch
            {
                try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); } catch { }
                throw;
            }
        }

        internal static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = System.IO.File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
    }
}
