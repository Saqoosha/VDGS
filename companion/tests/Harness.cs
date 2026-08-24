using System;
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
        Directory.Delete(dir, recursive: true);
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; } catch { return true; }
    }

    /// <summary>A one-shot loopback server, so the download path is exercised for real.</summary>
    private static IDisposable Serve(byte[] payload, out string url)
    {
        var port = 8971;
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
        Check(!TrackStore.Remove(db, "Official Course"), "refuses to remove a server track");
        Check(TrackStore.Find(db, "Official Course") != null, "the server track is still there");
        Check(TrackStore.Remove(db, "VDGS FDF"), "removes one it added");
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

        Console.WriteLine(_fail == 0 ? "\nALL PASS" : "\n" + _fail + " FAILED");
        return _fail == 0 ? 0 : 1;
    }
}
