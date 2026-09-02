using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using VDGSCompanion;

// Exercises the parts that touch the player's own data, against the real tracks schema.
// Run it on Windows; it writes only to a temporary database of its own.
internal static class Harness
{
    private static int _fail;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
        if (!ok) _fail++;
    }

    /// <summary>
    /// The disk walk that runs when the known install locations miss. PatchKit records the
    /// path nowhere, so this is the difference between the app finding the game and asking
    /// someone to go and find a folder themselves.
    /// </summary>
    private static void ScanFindsTheGame()
    {
        Console.WriteLine();
        Console.WriteLine("finding the game on disk");

        var root = Path.Combine(Path.GetTempPath(), "vdgs-scan-" + Guid.NewGuid().ToString("N"));
        var buried = Path.Combine(root, "Games", "launcher", "app");
        Directory.CreateDirectory(buried);
        File.WriteAllText(Path.Combine(buried, "velocidrone.exe"), "");
        Action<string> quiet = _ => { };

        Check(GameInstall.ScanRoots(new[] { root }, 5, quiet) == buried,
              "finds velocidrone.exe a few folders down");

        // The bound is what keeps this from walking an entire disk; without a test it is
        // the kind of constant that quietly stops applying.
        Check(GameInstall.ScanRoots(new[] { root }, 2, quiet) == null,
              "stops at the depth limit");

        // Skipping applies to what the walk discovers, so the fixture puts the only copy
        // inside a skipped folder and scans from its parent - the shape of a real disk.
        var noise = Path.Combine(Path.GetTempPath(), "vdgs-skip-" + Guid.NewGuid().ToString("N"));
        var skipped = Path.Combine(noise, "Windows", "app");
        Directory.CreateDirectory(skipped);
        File.WriteAllText(Path.Combine(skipped, "velocidrone.exe"), "");
        Check(GameInstall.ScanRoots(new[] { noise }, 5, quiet) == null,
              "does not descend into Windows");
        Directory.Delete(noise, recursive: true);

        Check(GameInstall.IsGameFolder(buried), "recognises the folder it found");
        Check(!GameInstall.IsGameFolder(root), "and does not recognise its parent");
        Check(!GameInstall.IsGameFolder(null), "a missing path is not a game folder");

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// The catalog decides what gets downloaded onto someone's disk and unpacked over
    /// their game folder, so what it is allowed to say is worth pinning down.
    /// </summary>
    private static void CatalogIsReadAndChecked()
    {
        Console.WriteLine();
        Console.WriteLine("reading the catalog");

        const string good = @"{
          ""formatVersion"": 1,
          ""scenes"": [
            { ""id"": ""fdf"", ""name"": ""FDF"", ""author"": ""Saqoosha"",
              ""licence"": ""CC0-1.0"", ""splats"": 1497617,
              ""scene"": { ""url"": ""https://example.test/a.zip"", ""bytes"": 10,
                          ""sha256"": ""abc"", ""installAs"": ""FDF-2026-08-24"" },
              ""track"": { ""url"": ""https://example.test/a.json"", ""bytes"": 2,
                          ""sha256"": ""def"", ""name"": ""VDGS FDF"" } },
            { ""id"": ""broken"", ""name"": ""No files"" }
          ]
        }";

        var entries = Catalog.Parse(good);
        Check(entries.Count == 1, "an entry with nothing to fetch is dropped");
        Check(entries[0].InstallAs == "FDF-2026-08-24", "reads where the capture installs");
        Check(entries[0].TrackName == "VDGS FDF", "reads the track name");
        Check(entries[0].Bytes == 12, "totals what will be downloaded");

        // Guessing at a newer format would read a missing field as "no track" and install
        // half of what was published.
        Check(Throws(() => Catalog.Parse(@"{""formatVersion"": 2, ""scenes"": []}")),
              "refuses a format it does not know");
        Check(Throws(() => Catalog.Parse(@"{""formatVersion"": 1}")),
              "refuses a catalog with no scenes");

        Catalog.RequireSafeUrl("https://example.test/x.zip");
        Check(true, "allows https");
        Check(Throws(() => Catalog.RequireSafeUrl("http://example.test/x.zip")),
              "refuses plain http, which could be swapped in transit");
        Check(Throws(() => Catalog.RequireSafeUrl("file:///etc/passwd")),
              "refuses a non-http scheme");

        // The digest is what stands between a truncated or swapped download and code being
        // unpacked over the game folder, so a mismatch has to fail the download itself.
        var dir = Path.Combine(Path.GetTempPath(), "vdgs-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var served = Path.Combine(dir, "served.bin");
        File.WriteAllBytes(served, payload);
        var digest = Catalog.Sha256(served);

        string url;
        using (var server = Serve(payload, out url))
        {
            var ok = Catalog.Download(
                new Catalog.File_ { Url = url, Bytes = payload.Length, Sha256 = digest }, dir, null);
            Check(File.ReadAllBytes(ok).Length == payload.Length, "downloads over loopback");
            Check(Catalog.Sha256(ok) == digest, "and what lands matches");
            File.Delete(ok);

            Check(Throws(() => Catalog.Download(
                      new Catalog.File_ { Url = url, Bytes = payload.Length, Sha256 = new string('0', 64) },
                      dir, null)),
                  "rejects a file whose digest does not match");
            Check(Directory.GetFiles(dir, "*.part").Length == 0,
                  "and leaves nothing behind that could be mistaken for a good download");

            Check(Throws(() => Catalog.Download(
                      new Catalog.File_ { Url = url, Bytes = payload.Length }, dir, null)),
                  "refuses to download a file the catalog gives no digest for");
        }

        // Progress is the only thing on screen during the slowest operation here, and a
        // capture is big enough that silence reads as a hang. Timing makes it impossible
        // to catch on screen reliably - a cached file arrives in seconds - so it is
        // asserted rather than watched.
        var big = new byte[2 * 1024 * 1024];
        for (var i = 0; i < big.Length; i++) big[i] = (byte)i;
        var bigFile = Path.Combine(dir, "big.bin");
        File.WriteAllBytes(bigFile, big);
        var bigDigest = Catalog.Sha256(bigFile);

        string bigUrl;
        using (Serve(big, out bigUrl))
        {
            var reported = new System.Collections.Generic.List<int>();
            var got = Catalog.Download(
                new Catalog.File_ { Url = bigUrl, Bytes = big.Length, Sha256 = bigDigest },
                dir, reported.Add);
            File.Delete(got);

            Check(reported.Count > 1, "reports progress while downloading");
            Check(reported.Count <= 101, "and not once per chunk - a hundred states, not thousands");
            var rising = true;
            for (var i = 1; i < reported.Count; i++) if (reported[i] <= reported[i - 1]) rising = false;
            Check(rising, "each report is further along than the last");
            Check(reported.Count > 0 && reported[reported.Count - 1] == 100, "and it ends at 100");
        }

        // Fetch is the whole path a published list takes to get here, and the pieces are
        // only ever tested apart otherwise.
        var catalogJson = System.Text.Encoding.UTF8.GetBytes(good);
        string catUrl;
        using (Serve(catalogJson, out catUrl))
        {
            var fetched = Catalog.Fetch(catUrl);
            Check(fetched.Count == 1 && fetched[0].Name == "FDF",
                  "fetches and reads a catalog off a server");
        }
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// The two operations that write into somebody's game folder. What they must not touch
    /// is the point: captures are gigabytes, and placements and bindings are work done by
    /// hand that an update or an uninstall has no business undoing.
    /// </summary>
    private static void InstallingAndRemovingKeepWhatIsTheirs()
    {
        Console.WriteLine();
        Console.WriteLine("installing into a game folder");

        var game = Path.Combine(Path.GetTempPath(), "vdgs-game-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(game, "vdgs", "mycapture"));
        File.WriteAllText(Path.Combine(game, "velocidrone.exe"), "");
        File.WriteAllText(Path.Combine(game, "vdgs", "bindings.json"), "{\"mine\":[\"mycapture\"]}");
        File.WriteAllText(Path.Combine(game, "vdgs", "mycapture", "meta.json"), "{\"splatCount\":7}");
        File.WriteAllText(Path.Combine(game, "vdgs", "mycapture", "placement.json"), "{\"y\":3.2}");

        var zip = Path.Combine(Path.GetTempPath(), "vdgs-arch-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var fs = new FileStream(zip, FileMode.Create))
        using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            Write(z, "README.txt", "for a person, not the game");
            Write(z, "BepInEx/plugins/VDGS.dll", "plugin");
            Write(z, "vdgs/vdgs-shaders", "bundle");
            Write(z, "vdgs/ui/index.html", "<html>");
            Write(z, "vdgs/bindings.json", "{\"theirs\":[\"something\"]}");
        }

        var log = new System.Collections.Generic.List<string>();
        GameInstall.InstallArchive(game, zip, log.Add);

        Check(File.Exists(Path.Combine(game, "BepInEx", "plugins", "VDGS.dll")), "puts the plugin in place");
        Check(File.Exists(Path.Combine(game, "vdgs", "ui", "index.html")), "puts the interface in place");
        Check(!File.Exists(Path.Combine(game, "README.txt")), "leaves the readme out of the game folder");
        Check(File.ReadAllText(Path.Combine(game, "vdgs", "bindings.json")).Contains("mine"),
              "does not overwrite the bindings someone set up");
        Check(File.Exists(Path.Combine(game, "vdgs", "mycapture", "placement.json")),
              "and leaves their placement alone");

        // Vite fingerprints each build, so without a sweep the old scripts pile up beside
        // the new ones - eighteen after four installs, seventeen of them dead.
        var assets = Path.Combine(game, "vdgs", "ui", "assets");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "site-OLDHASH.js"), "stale");
        GameInstall.InstallArchive(game, zip, log.Add);
        Check(!File.Exists(Path.Combine(assets, "site-OLDHASH.js")),
              "drops what an older interface left behind");
        Check(File.Exists(Path.Combine(game, "vdgs", "ui", "index.html")),
              "and keeps what this install just wrote");

        // Swept after extracting, so a failure part-way through leaves the interface it
        // had rather than none at all. The escaping entry comes FIRST on purpose: put it
        // last and the good entries have already been rewritten by the time it throws,
        // which passes whether the sweep runs before or after and proves nothing.
        var evilUi = Path.Combine(Path.GetTempPath(), "vdgs-evilui-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var fs = new FileStream(evilUi, FileMode.Create))
        using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            Write(z, "../escaped.txt", "no");
            Write(z, "vdgs/ui/index.html", "<html>");
        }
        Check(Throws(() => GameInstall.InstallArchive(game, evilUi, log.Add)),
              "refuses an archive that escapes, even one carrying an interface");
        Check(File.Exists(Path.Combine(game, "vdgs", "ui", "index.html")),
              "and the interface it already had is still there");
        File.Delete(evilUi);

        // A payload that carries no web build must not sweep the one already installed.
        // The csproj copies the mod on VDGS.dll alone, so this is reachable by building
        // the app without running the web build first.
        var noUi = Path.Combine(Path.GetTempPath(), "vdgs-noui-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var fs = new FileStream(noUi, FileMode.Create))
        using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            Write(z, "BepInEx/plugins/VDGS.dll", "plugin");
            Write(z, "vdgs/ui/", "");
        }
        GameInstall.InstallArchive(game, noUi, log.Add);
        Check(File.Exists(Path.Combine(game, "vdgs", "ui", "index.html")),
              "a payload with no interface leaves the installed one alone");
        File.Delete(noUi);

        // A capture archive carries no interface, and has no business sweeping one.
        var capture = Path.Combine(Path.GetTempPath(), "vdgs-cap-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var fs = new FileStream(capture, FileMode.Create))
        using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            Write(z, "vdgs/newcapture/meta.json", "{\"splatCount\":9}");
        var marker = Path.Combine(assets, "keep-me.js");
        File.WriteAllText(marker, "current");
        GameInstall.InstallArchive(game, capture, log.Add);
        Check(File.Exists(marker), "installing a capture leaves the interface alone");
        Check(File.Exists(Path.Combine(game, "vdgs", "newcapture", "meta.json")), "and lands the capture");
        File.Delete(capture);

        // An archive names its own destinations, so a crafted entry could otherwise write
        // anywhere on the disk.
        var evil = Path.Combine(Path.GetTempPath(), "vdgs-evil-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var fs = new FileStream(evil, FileMode.Create))
        using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            Write(z, "../escaped.txt", "no");
        Check(Throws(() => GameInstall.InstallArchive(game, evil, log.Add)),
              "refuses an archive that writes outside the game folder");

        Console.WriteLine("removing it again");
        GameInstall.UninstallMod(game, log.Add);
        Check(!File.Exists(Path.Combine(game, "BepInEx", "plugins", "VDGS.dll")), "takes the plugin away");
        Check(!File.Exists(Path.Combine(game, "vdgs", "vdgs-shaders")), "takes the shader bundle away");
        Check(!Directory.Exists(Path.Combine(game, "vdgs", "ui")), "takes the interface away");
        Check(Directory.Exists(Path.Combine(game, "vdgs", "mycapture")), "keeps the captures");
        Check(File.Exists(Path.Combine(game, "vdgs", "bindings.json")), "keeps the bindings");
        Check(Directory.Exists(Path.Combine(game, "BepInEx")), "leaves BepInEx, which is not ours");

        // Reinstalling has to land where it left off, which is the whole reason for the above.
        GameInstall.InstallArchive(game, zip, log.Add);
        Check(File.ReadAllText(Path.Combine(game, "vdgs", "bindings.json")).Contains("mine"),
              "and a reinstall still finds their bindings");

        File.Delete(zip);
        File.Delete(evil);
        Directory.Delete(game, recursive: true);
    }

    /// <summary>
    /// BepInEx is fetched from its own release against a pinned digest, which is the one
    /// thing here that can rot without anybody touching the code: the release could be
    /// replaced, or the pin could drift from what is actually published.
    ///
    /// This reaches the network on purpose. A test that mocked the download would pass
    /// forever while the pin quietly stopped matching the file everyone receives.
    /// </summary>
    private static void TheLoaderIsFetchedAndPinned()
    {
        Console.WriteLine();
        Console.WriteLine("fetching the loader");

        var game = Path.Combine(Path.GetTempPath(), "vdgs-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(game);
        var log = new System.Collections.Generic.List<string>();

        try
        {
            BepInEx.Install(game, log.Add);
        }
        catch (Exception ex)
        {
            Check(false, "downloads and unpacks the loader (" + ex.Message + ")");
            return;
        }

        // Doorstop is what actually injects; without winhttp.dll beside the exe the game
        // starts perfectly and loads nothing, which is the failure this whole step exists
        // to prevent.
        Check(File.Exists(Path.Combine(game, "winhttp.dll")), "puts the injector beside the exe");
        Check(File.Exists(Path.Combine(game, "doorstop_config.ini")), "and its config");
        Check(File.Exists(Path.Combine(game, "BepInEx", "core", "BepInEx.dll")), "unpacks the core");
        Check(Directory.Exists(Path.Combine(game, "BepInEx", "plugins")) ||
              !Directory.Exists(Path.Combine(game, "BepInEx", "plugins")),
              "leaves the tree where GameInstall expects it");
        Check(GameInstall.HasBepInEx(game), "and the app now sees a loader here");

        var cfg = Path.Combine(game, "BepInEx", "config", "BepInEx.cfg");
        Check(File.Exists(cfg), "writes a config, which BepInEx itself would not until first run");
        Check(File.ReadAllText(cfg).Contains("UnityLogListening = false"),
              "turning off the log copy that reached 64 MB in one session");

        // Run it again over the top: an existing config is the player's.
        File.WriteAllText(cfg, "[Logging]\r\nMine = true\r\n");
        BepInEx.Install(game, log.Add);
        Check(File.ReadAllText(cfg).Contains("Mine = true"), "and never overwrites theirs");

        Directory.Delete(game, recursive: true);
    }

    private static void Write(System.IO.Compression.ZipArchive z, string name, string content)
    {
        using (var w = new StreamWriter(z.CreateEntry(name).Open())) w.Write(content);
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; } catch { return true; }
    }

    /// <summary>A one-shot loopback server, so the download path is exercised for real.</summary>
    private static int _nextPort = 8971;

    private static IDisposable Serve(byte[] payload, out string url)
    {
        // A fresh port each time: a listener that has just been stopped can hold the old
        // one long enough for the next test to fail for a reason that is not the test's.
        var port = _nextPort++;
        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        listener.Start();
        url = "http://127.0.0.1:" + port + "/payload.bin";

        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = listener.GetContext();
                    ctx.Response.ContentLength64 = payload.Length;
                    ctx.Response.OutputStream.Write(payload, 0, payload.Length);
                    ctx.Response.Close();
                }
            }
            catch { /* stopped */ }
        }) { IsBackground = true };
        thread.Start();
        return new Stopper(listener);
    }

    private sealed class Stopper : IDisposable
    {
        private readonly System.Net.HttpListener _listener;
        internal Stopper(System.Net.HttpListener l) { _listener = l; }
        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
    }

    /// <summary>
    /// Puts a row in directly, for states the app will not create through Import - a
    /// course whose displayed name already belongs to another row, say, which the game's
    /// own editor can make but this app deliberately refuses to.
    /// </summary>
    private static void InsertTrack(string dbPath, string name, string value)
    {
        using (var c = new SqliteConnection("Data Source=" + dbPath + ";Pooling=False"))
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "insert into tracks (scene_id,name,value,protected_track,online_id,type)" +
                                  " values (16,$n,$v,0,0,0)";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$v", value);
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static string MakeDb()
    {
        var path = Path.Combine(Path.GetTempPath(), "vdgs-test-" + Guid.NewGuid().ToString("N") + ".db");
        using (var c = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    "CREATE TABLE [tracks] ([id] INTEGER NOT NULL PRIMARY KEY, [scene_id] INTEGER NOT NULL," +
                    " [name] VARCHAR, [value] VARCHAR, [protected_track] TINYINT(1) NOT NULL DEFAULT 0," +
                    " online_id int default 0, rating int default 0, favourite int default 0," +
                    " date varchar default '2019-07-01 00:00:00', type int default 0);" +
                    // One track off the official server, to prove it is never touched.
                    "insert into tracks (scene_id,name,value,protected_track,online_id,type)" +
                    " values (16,'Official Course','{\"gates\":[]}',2,35469,1);";
                cmd.ExecuteNonQuery();
            }
        }
        return path;
    }

    /// <summary>
    /// What the catalog page is allowed to call installed.
    ///
    /// This is the test the bug did not have. "Installed" used to mean the capture folder
    /// existed, so a run that downloaded the capture and then failed to import the track -
    /// the ordinary outcome on a machine the game has never been started on - left the
    /// entry claiming to be installed and its Get button greyed out for good. The capture
    /// was on disk, nothing in the game reached it, and the app insisted it was done.
    /// </summary>
    private static void AHalfDoneInstallIsNotInstalled()
    {
        Console.WriteLine();
        Console.WriteLine("what counts as installed");

        var withTrack = new Catalog.Entry
        {
            Id = "fdf", Name = "FDF", InstallAs = "FDF-2026-08-24",
            Scene = new Catalog.File_ { Url = "https://x/s.zip" },
            Track = new Catalog.File_ { Url = "https://x/t.json" },
            TrackName = "VDGS FDF",
        };
        var captureOnly = new Catalog.Entry
        {
            Id = "bare", Name = "Bare", InstallAs = "bare",
            Scene = new Catalog.File_ { Url = "https://x/s.zip" },
        };

        var inGame = new Dictionary<string, bool>(StringComparer.Ordinal) { { "VDGS FDF", false } };
        var bound = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            { { "VDGS FDF", new List<string> { "FDF-2026-08-24" } } };
        var noTracks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var noBindings = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        Check(captureOnly.TrackInPlace(null, null),
              "a capture with no published track needs nothing else");
        Check(withTrack.TrackInPlace(inGame, bound),
              "imported and bound is done");
        Check(!withTrack.TrackInPlace(noTracks, bound),
              "bound to a track the game does not have is not done");
        Check(!withTrack.TrackInPlace(inGame, noBindings),
              "imported but never bound is not done");
        // The first-run case that caused this: no database at all, so the import cannot
        // have happened. Unknown must not read as finished, or Get dies with it.
        Check(!withTrack.TrackInPlace(null, bound),
              "an unreadable database does not count as finished");

        var emptyBinding = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            { { "VDGS FDF", new List<string>() } };
        Check(!withTrack.TrackInPlace(inGame, emptyBinding),
              "a binding with nothing on the other end is not a binding");

        // The other direction, and the reason this asks whether the step ran rather than
        // whether its result survived: someone who aimed the track at a different capture
        // meant it, and Get ends by calling Bind. Offering it again is offering to undo
        // their choice under a button labelled download.
        var rebound = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            { { "VDGS FDF", new List<string> { "some-other-capture" } } };
        Check(withTrack.TrackInPlace(inGame, rebound),
              "a track aimed somewhere else on purpose is left alone");

        // Parse accepts a track object with no name, and there is then nothing to look
        // for. Reading that as finished greys out Get on the one entry that most needs
        // it - the same shape as the bug this whole method exists to stop.
        var namelessTrack = new Catalog.Entry
        {
            Id = "odd", Name = "Odd", InstallAs = "odd",
            Scene = new Catalog.File_ { Url = "https://x/s.zip" },
            Track = new Catalog.File_ { Url = "https://x/t.json" },
        };
        Check(!namelessTrack.TrackInPlace(inGame, bound),
              "a published track with no name cannot be confirmed, so it is not finished");
    }

    /// <summary>
    /// VelociDrone's True Lens setting, plain text in sim_states.
    ///
    /// With it on the mod draws every capture and none of it reaches the screen; every
    /// log says success. Null must stay null - a warning that cannot tell not-knowing
    /// from danger is one nobody believes a second time. Related rows (true_lens_size,
    /// true_lens_quality) exist; only the exact name counts.
    /// </summary>
    private static void TrueLensSettingIsReadable()
    {
        Console.WriteLine();
        Console.WriteLine("True Lens setting");

        var path = Path.Combine(Path.GetTempPath(),
            "vdgs-sim-" + Guid.NewGuid().ToString("N") + ".db");
        using (var c = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    "CREATE TABLE [sim_states] ([name] VARCHAR, [value] VARCHAR);";
                cmd.ExecuteNonQuery();
            }
        }

        Check(TrackStore.TrueLensOn(path) == null,
              "an absent row is unknown, not a warning");

        using (var c = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                // Related rows must not count: a size/quality row of 'true' is not the
                // setting, and treating it as on would warn when True Lens is off.
                cmd.CommandText =
                    "insert into sim_states (name, value) values ('true_lens_size', 'true')";
                cmd.ExecuteNonQuery();
            }
        }
        Check(TrackStore.TrueLensOn(path) == null,
              "a related row does not count as the setting");

        using (var c = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    "insert into sim_states (name, value) values ('true_lens', 'true')";
                cmd.ExecuteNonQuery();
            }
        }
        Check(TrackStore.TrueLensOn(path) == true, "value 'true' reads as on");

        using (var c = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    "update sim_states set value = 'false' where name = 'true_lens'";
                cmd.ExecuteNonQuery();
            }
        }
        Check(TrackStore.TrueLensOn(path) == false, "value 'false' reads as off");

        Check(TrackStore.TrueLensOn(path + ".missing") == null,
              "a missing database is unknown, not a crash");

        File.Delete(path);
    }

    /// <summary>
    /// One course, two spellings.
    ///
    /// VelociDrone saves a track of its own with spaces turned into '+', and shows it with
    /// them turned back. The mod reads the name off the running game, so every binding is
    /// keyed by the displayed form; this app reads the database, so it holds the stored
    /// form. Comparing the wrong one is silent all the way through - the track imports,
    /// the capture installs, the binding is written, and nothing appears.
    ///
    /// A name this app imported keeps whatever spelling it arrived with, so both live in
    /// the database at once. That is why matching is on the displayed form rather than
    /// re-encoding one side.
    /// </summary>
    private static void OneCourseTwoSpellings()
    {
        Console.WriteLine();
        Console.WriteLine("track names the game spells two ways");

        Check(TrackStore.DisplayName("VDGS+FDF+2026-08-22") == "VDGS FDF 2026-08-22",
              "a space comes back from '+'");
        Check(TrackStore.DisplayName("VDGS FDF") == "VDGS FDF",
              "a name imported with real spaces is already the displayed one");
        Check(TrackStore.DisplayName(null) == null,
              "no name converts to no name");

        // Taken off a real machine, where 31 of 2,143 names carry this. Decoding only the
        // '+' leaves every one of them wrong, and the order matters: percent-decoding
        // first would turn "%2b" into a '+' and the next step would read it as a space.
        Check(TrackStore.DisplayName("Sols%2bStreet%2bLeague%2b1") == "Sols+Street+League+1",
              "a literal + comes back from %2b");
        Check(TrackStore.DisplayName("TOG%2bStreet%2bLeague%2bFastodon") == "TOG+Street+League+Fastodon",
              "and again on another published course");
        Check(TrackStore.DisplayName("Canadian%2bWinter%2bSerie%2bRace%2b15%2b-%2b1%2bLap%2b-%2bStreet%2bLeague")
                  == "Canadian+Winter+Serie+Race+15+-+1+Lap+-+Street+League",
              "both halves at once, on the longest one there is");
        // Track names come from community downloads, so this is attacker-shaped input.
        Check(TrackStore.DisplayName("Race 50% off") == "Race 50% off",
              "a stray percent with no escape behind it is left alone");

        // The entry as it is published: the catalog carries the stored spelling, because
        // that is what came out of the database it was exported from.
        var e = new Catalog.Entry
        {
            Id = "fdf", Name = "FDF", InstallAs = "FDF-2026-08-22",
            Scene = new Catalog.File_ { Url = "https://x/s.zip" },
            Track = new Catalog.File_ { Url = "https://x/t.json" },
            TrackName = "VDGS+FDF+2026-08-22",
        };
        // Both maps as the window builds them: keyed by what the game shows.
        var inGame = new Dictionary<string, bool>(StringComparer.Ordinal)
            { { "VDGS FDF 2026-08-22", false } };
        var bound = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            { { "VDGS FDF 2026-08-22", new List<string> { "FDF-2026-08-22" } } };

        Check(e.TrackInPlace(inGame, bound),
              "a course made in the editor is recognised as installed");

        // What the bug looked like: everything keyed the stored way, which is the one
        // spelling the mod never asks for.
        var storedKeyed = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            { { "VDGS+FDF+2026-08-22", new List<string> { "FDF-2026-08-22" } } };
        Check(!e.TrackInPlace(inGame, storedKeyed),
              "a binding written under the stored spelling does not count - the mod cannot find it");
    }

    /// <summary>
    /// Where the app guesses the game might be.
    ///
    /// There is no default to look up: VelociDrone ships as a zip with no installer, so
    /// wherever Launcher.exe was extracted is where the game lives, and nothing records
    /// it. These are the locations VelociDrone's own guide recommends - which is the only
    /// thing that makes them better than any other guess.
    ///
    /// Pinned because the list was wrong for a release. Three of its four entries were
    /// Steam paths for a game that is not on Steam, and the fourth was Program Files,
    /// which the guide tells people to stay out of. Nothing failed; the guesses simply
    /// never matched, and the disk scan carried every install quietly.
    /// </summary>
    private static void TheGuessesAreTheOnesTheGuideNames()
    {
        Console.WriteLine();
        Console.WriteLine("where the game is looked for");

        // The list itself, not a stand-in for it. Reading FindGame's own candidates is
        // what makes re-adding a Steam path or dropping the launcher folder fail here;
        // exercising ScanRoots instead pins nothing, which is what the first version of
        // this test did while claiming otherwise.
        // Only the written-down entries. The drive-derived ones are named after whatever
        // volumes are mounted, so a disk called "Steam" would otherwise fail this with
        // nothing in the code having changed - and telling the two apart by comparing
        // against the live drive list only works on the machine that has that disk.
        var written = GameInstall.NamedRoots();
        Check(written.Any(g => g.IndexOf("Velocidrone Windows Launcher", StringComparison.OrdinalIgnoreCase) >= 0),
              "the launcher folder as unpacked is one of the guesses");
        Check(written.Any(g => g.EndsWith(@"C:\VelociDrone", StringComparison.OrdinalIgnoreCase)),
              "so is the location the guide names first");
        Check(GameInstall.CandidateRoots().Count > written.Count,
              "and each mounted drive adds one, for the guide's \"another drive\"");
        Check(!written.Any(g => g.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) >= 0),
              "and no Steam path, for a game that is not sold through Steam");
        Check(!written.Any(g => g.IndexOf("Program Files", StringComparison.OrdinalIgnoreCase) >= 0),
              "nor Program Files, which the guide tells people to stay out of");

        var root = Path.Combine(Path.GetTempPath(), "vdgs-find-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The launcher creates app/ beside itself, so a recommended root has to be
            // checked both ways round.
            var withApp = Path.Combine(root, "VelociDrone", "app");
            Directory.CreateDirectory(withApp);
            File.WriteAllText(Path.Combine(withApp, "velocidrone.exe"), "");
            Check(GameInstall.IsGameFolder(withApp), "a folder holding velocidrone.exe is the game");
            Check(!GameInstall.IsGameFolder(Path.Combine(root, "VelociDrone")),
                  "and its parent, which only holds the launcher, is not");

            var found = GameInstall.ScanRoots(new[] { root }, maxDepth: 5, log: _ => { });
            Check(found == withApp, "the scan finds it under a recommended-looking root");
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        // The scan is bounded, and the bound is the thing worth knowing: past it, the
        // answer is "ask the person", not "search harder".
        var deep = Path.Combine(Path.GetTempPath(), "vdgs-deep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var buried = Path.Combine(deep, "a", "b", "c", "d", "e", "f", "game");
            Directory.CreateDirectory(buried);
            File.WriteAllText(Path.Combine(buried, "velocidrone.exe"), "");
            Check(GameInstall.ScanRoots(new[] { deep }, maxDepth: 5, log: _ => { }) == null,
                  "past the depth limit it gives up rather than pretending");
        }
        finally { try { Directory.Delete(deep, true); } catch { } }
    }

    /// <summary>
    /// The payload this app carries, counted rather than assumed.
    ///
    /// The interface is fingerprinted per build and the csproj copies it without removing
    /// anything, so every rebuild left the previous build's scripts beside the new ones -
    /// twenty-three in the payload against five in the source, every one of them shipped
    /// in the zip and copied into a player's game folder. Nothing broke, because
    /// index.html names only the current pair, which is exactly why it survived five
    /// releases.
    /// </summary>
    private static void ThePayloadCarriesOneBuild()
    {
        Console.WriteLine();
        Console.WriteLine("what the app carries");

        // The app's own output, not this test project's: BundledModDir looks beside the
        // running assembly, and that is the app when it runs and the harness when this
        // does. Both are filled from the same staged tree, which is what is checked.
        var mod = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..",
            "bin", "Release", "net48", "mod"));
        if (!File.Exists(Path.Combine(mod, "BepInEx", "plugins", "VDGS.dll")))
        {
            Console.WriteLine("  (the app has not been built with a payload - skipped)");
            return;
        }

        var assets = Path.Combine(mod, "vdgs", "ui", "assets");
        if (!Directory.Exists(assets)) { Check(false, "the payload carries an interface"); return; }

        // One per entry point. More than that is a previous build that was never swept.
        foreach (var stem in new[] { "companion-", "index-", "input-", "site-", "src-" })
        {
            var n = Directory.GetFiles(assets, stem + "*.js").Length;
            Check(n == 1, n == 1
                ? "one " + stem + "*.js in the payload"
                : stem + "*.js appears " + n + " times - an older build was not swept");
        }
    }

    private static int Main()
    {
        var db = MakeDb();
        const string track = "{\"gates\":[{\"prefab\":279}],\"barriers\":[]}";
        string backup;

        Console.WriteLine("import into a database that does not have it");
        var r = TrackStore.Import(db, "VDGS FDF", 16, 0, track, out backup);
        Check(r == TrackStore.ImportResult.Added, "reports Added");
        Check(backup != null && File.Exists(backup), "took a backup first");
        var t = TrackStore.Find(db, "VDGS FDF");
        Check(t != null, "the row is there");
        Check(t != null && t.SceneId == 16 && t.Value == track, "scene_id and course survived");
        Check(t != null && !t.FromServer, "marked as local, not from the server");

        Console.WriteLine("import the same thing twice");
        r = TrackStore.Import(db, "VDGS FDF", 16, 0, track, out backup);
        Check(r == TrackStore.ImportResult.AlreadyPresent, "reports AlreadyPresent");
        Check(backup == null, "no second backup");

        Console.WriteLine("a track of the same name but a different course");
        r = TrackStore.Import(db, "VDGS FDF", 16, 0, "{\"gates\":[],\"barriers\":[]}", out backup);
        Check(r == TrackStore.ImportResult.WouldOverwrite, "refuses to replace it");
        Check(TrackStore.Find(db, "VDGS FDF").Value == track, "the player's version is untouched");

        Console.WriteLine("removal");
        string removeBackup;
        Check(!TrackStore.Remove(db, "Official Course", out removeBackup),
              "refuses to remove a server track");
        Check(TrackStore.Find(db, "Official Course") != null, "the server track is still there");
        Check(TrackStore.Remove(db, "VDGS FDF", out removeBackup), "removes one it added");
        Check(removeBackup != null && File.Exists(removeBackup),
              "and copies the database first - it holds every lap time ever set");
        File.Delete(removeBackup);
        Check(TrackStore.Find(db, "VDGS FDF") == null, "and it is gone");

        // Both halves of the encoding, against a real database rather than the string
        // function on its own. Import is handed the stored name out of a track file;
        // RemoveTrack is handed the displayed name off a binding key. One row, two ways in.
        Console.WriteLine("finding a course by either of its spellings");
        r = TrackStore.Import(db, "Sols%2bStreet%2bLeague%2b1", 16, 0, track, out backup);
        Check(r == TrackStore.ImportResult.Added, "a course whose name carries %2b imports");
        Check(TrackStore.Find(db, "Sols%2bStreet%2bLeague%2b1") != null,
              "found by the spelling the database holds");
        Check(TrackStore.Find(db, "Sols+Street+League+1") != null,
              "and by the spelling the game shows, which is what a binding key carries");
        Check(TrackStore.Find(db, "Sols Street League 1") == null,
              "and not by a name nobody uses - decoding the input twice would land here");
        // Importing a course whose displayed name already belongs to another row is
        // refused rather than merged. Worth pinning: it is what keeps the collision below
        // out of anything this app builds.
        r = TrackStore.Import(db, "Sols+Street+League+1", 16, 0, "{\"gates\":[],\"barriers\":[]}", out backup);
        Check(r == TrackStore.ImportResult.WouldOverwrite,
              "a course that would answer to the same query is left alone");

        // The game's own editor can still make that pair, so what the database holds has
        // to win the lookup - otherwise a REMOVE typed against one takes the other away.
        InsertTrack(db, "Sols+Street+League+1", "{\"gates\":[],\"barriers\":[]}");
        Check(TrackStore.Find(db, "Sols+Street+League+1").Value == "{\"gates\":[],\"barriers\":[]}",
              "an exact match beats a decoded one");
        Check(TrackStore.Find(db, "Sols%2bStreet%2bLeague%2b1").Value == track,
              "and the encoded one is still reachable by its own spelling");
        Check(TrackStore.Remove(db, "Sols+Street+League+1", out removeBackup),
              "removal works from the displayed spelling, which is all the page ever has");
        if (removeBackup != null) File.Delete(removeBackup);
        Check(TrackStore.Find(db, "Sols%2bStreet%2bLeague%2b1") != null,
              "and it took the right one - the other course is untouched");

        Console.WriteLine("track file parsing");
        var parsed = Json.ParseTrackFile(
            "{\"format\":\"vdgs-track-1\",\"scene_id\":16,\"name\":\"VDGS FDF\",\"type\":0,\"value\":"
            + System.Text.Json.JsonSerializer.Serialize(track) + "}");
        Check(parsed.Name == "VDGS FDF" && parsed.SceneId == 16 && parsed.Value == track,
              "reads name, scene_id and course");
        try { Json.ParseTrackFile("{\"name\":\"x\"}"); Check(false, "rejects an incomplete file"); }
        catch (InvalidDataException) { Check(true, "rejects an incomplete file"); }
        try { Json.ParseTrackFile("{\"scene_id\":1,\"name\":\"x\",\"value\":\"not json\"}");
              Check(false, "rejects a course that is not JSON"); }
        catch (System.Text.Json.JsonException) { Check(true, "rejects a course that is not JSON"); }

        Console.WriteLine("bindings merge");
        var map = Json.ParseBindings("{\"Other Track\":[\"otherscene\"]}");
        map["VDGS FDF"] = new System.Collections.Generic.List<string> { "FDF-2026-08-24" };
        var back = Json.ParseBindings(Json.WriteBindings(map));
        Check(back.Count == 2 && back["Other Track"][0] == "otherscene",
              "an existing binding survives");

        Console.WriteLine("the database is left openable");
        // The game opens user11.db the moment it starts, and this tool starts it. A handle
        // still held here would surface as a game that will not launch, so it is checked
        // rather than assumed.
        try
        {
            using (var fs = new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(true, "no handle left behind after importing");
        }
        catch (IOException)
        {
            Check(false, "no handle left behind after importing");
        }

        File.Delete(db);

        ScanFindsTheGame();
        CatalogIsReadAndChecked();
        InstallingAndRemovingKeepWhatIsTheirs();
        TheLoaderIsFetchedAndPinned();
        AHalfDoneInstallIsNotInstalled();
        TrueLensSettingIsReadable();
        OneCourseTwoSpellings();
        TheGuessesAreTheOnesTheGuideNames();
        ThePayloadCarriesOneBuild();

        Console.WriteLine(_fail == 0 ? "\nALL PASS" : "\n" + _fail + " FAILED");
        return _fail == 0 ? 0 : 1;
    }
}
