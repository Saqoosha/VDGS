using System;
using System.Collections.Generic;
using System.IO;
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

        Console.WriteLine(_fail == 0 ? "\nALL PASS" : "\n" + _fail + " FAILED");
        return _fail == 0 ? 0 : 1;
    }
}
