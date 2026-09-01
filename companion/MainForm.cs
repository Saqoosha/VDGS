using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VDGSCompanion
{
    /// <summary>
    /// One window: what is installed, what is missing, and a button that fixes it.
    ///
    /// The audience is someone who wants to fly, not to read a setup guide, so state is
    /// shown rather than assumed - a player who cannot tell whether the mod is installed
    /// ends up reinstalling over a working setup.
    ///
    /// The window is a WebView2 showing the same React app the mod serves in the browser
    /// (web/, built to ui/ beside the exe) - one page of it, the setup page. The file and
    /// database work stays in C# where it is tested; the page only sends command names
    /// back and renders the state it is given.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly WebView2 _web = new WebView2 { Dock = DockStyle.Fill };
        private readonly JsonSerializerOptions _json =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private string _game;
        private bool _ready;   // the page is up and can be posted to
        private string _busy;  // what is being done right now, or null
        private int? _busyPercent;
        private readonly Settings _settings;
        private List<Catalog.Entry> _catalog;
        private string _catalogError;
        // What the page was last told about the game, so a tick only speaks when it changes.
        private bool _running;
        // Nothing tells this app that VelociDrone exited, so it looks. Half the window is
        // about files the game holds open, and Fly turns itself off while it is up - all
        // of that stays wrong until someone thinks to press refresh, which nobody does.
        private readonly System.Windows.Forms.Timer _watch =
            new System.Windows.Forms.Timer { Interval = 1500 };

        internal MainForm()
        {
            Text = "VDGS Companion";
            // Tall enough for the whole page, but never taller than the desktop it opens
            // on - a window that starts with its own controls under the taskbar reads as
            // broken before anything has been done with it.
            var work = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(780, work.Width - 80), Math.Min(880, work.Height - 60));
            MinimumSize = new Size(560, 520);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(0x05, 0x07, 0x0c);   // matches the page, so no white flash
            Controls.Add(_web);

            // A remembered path first: someone who keeps the game somewhere unusual should
            // not have to say so twice. It is checked, not trusted - drives get unplugged.
            _settings = Settings.Load();
            _game = GameInstall.IsGameFolder(_settings.Game)
                ? _settings.Game
                : GameInstall.FindGame();
            Load += async (s, e) => await Start();
            _watch.Tick += (s, e) => WatchGame();
            FormClosed += (s, e) => _watch.Stop();
        }

        /// <summary>
        /// Running is one bool, and building a whole state walks every capture on disk -
        /// so the game starting is sent on its own. Quitting is not the same: the game
        /// owns the track database while it is up, and the browser UI can bind a capture
        /// while someone is flying, so what is on disk afterwards may not be what this
        /// window is showing. That one earns a fresh state, and it happens with nobody
        /// touching the window.
        ///
        /// It runs on the UI thread, and that is deliberate. Moving it to the pool to
        /// keep the window painting was tried and taken back out: nothing on a pool
        /// thread guards the walk, and an unhandled I/O error there takes the whole
        /// process down without a word, where on this thread WinForms catches it. A
        /// window that stops painting for as long as the walk takes is the smaller
        /// failure, so the walk stays here and the pause is a known cost.
        /// </summary>
        private void WatchGame()
        {
            var now = GameInstall.IsRunning();
            if (now == _running) return;
            _running = now;
            if (now) Post(new { type = "running", running = true });
            else Push();
        }

        private async System.Threading.Tasks.Task Start()
        {
            try
            {
                // The default user-data folder sits beside the exe, which may be read-only
                // if this was unzipped into Program Files.
                var data = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VDGSCompanion", "webview");
                var env = await CoreWebView2Environment.CreateAsync(null, data);
                await _web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                // WebView2 ships with Windows 11 and with Edge on Windows 10, so this is
                // rare - but silently showing an empty window would be worse than saying so.
                MessageBox.Show(this,
                    "This needs the Microsoft Edge WebView2 Runtime, which could not be started.\n\n" +
                    ex.Message + "\n\nInstall it from https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                    "VDGS", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            var c = _web.CoreWebView2;
            c.Settings.AreDefaultContextMenusEnabled = false;
            c.Settings.AreDevToolsEnabled = false;
            c.Settings.IsStatusBarEnabled = false;
            c.Settings.IsZoomControlEnabled = false;
            // Nothing in the page links out, so anything that tries is not ours.
            c.NewWindowRequested += (s, e) => e.Handled = true;

            c.WebMessageReceived += (s, e) => Dispatch(e.WebMessageAsJson);
            c.NavigationCompleted += (s, e) =>
            {
                _ready = true;
                Push();
                _watch.Start();
                FindGameIfMissing();
            };

            // The page is a built bundle of ES modules, which a file:// or
            // NavigateToString origin will not load. A virtual host gives it a real one
            // without opening a port on the machine.
            var ui = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui");
            if (!File.Exists(Path.Combine(ui, "companion.html")))
            {
                MessageBox.Show(this,
                    "The interface files are missing.\n\nExpected: " + ui +
                    "\n\nBuild them with: cd web && bun run build",
                    "VDGS", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            c.SetVirtualHostNameToFolderMapping(
                VirtualHost, ui, CoreWebView2HostResourceAccessKind.Allow);
            c.Navigate("http://" + VirtualHost + "/companion.html");
        }

        // Any name works as long as nothing else claims it; .invalid is reserved by RFC
        // 2606 precisely so it can never resolve to a real machine.
        private const string VirtualHost = "vdgs.invalid";

        // ------------------------------------------------------------------ host -> page

        private void Post(object payload)
        {
            // Work runs off the UI thread so the window keeps drawing; the WebView may
            // only be touched from the thread that owns it. Closing the window mid-job is
            // ordinary, and a job still posting into a disposed form is not a crash worth
            // showing anyone.
            if (InvokeRequired) { OnUi(() => Post(payload)); return; }
            if (_ready) _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, _json));
        }

        private void OnUi(Action a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(a);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void Log(string line) =>
            Post(new { type = "log", line = DateTime.Now.ToString("HH:mm:ss") + "  " + line });

        private void Push()
        {
            // Measured rather than assumed: this walk is the reason the busy message goes
            // out on its own, and if it ever grows past a moment that is worth knowing
            // from a real machine rather than from a guess.
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var missing = new List<string>();
            var scenes = new List<GameInstall.SceneInfo>();
            var tracks = new List<object>();
            var unbound = new List<object>();
            string mod = null;
            // Read once and handed to both readers: the track list and the catalog now ask
            // the same two questions, and asking them twice means opening the database
            // twice inside a walk that is already the slow part of this method.
            Dictionary<string, bool> inGame = null;
            Dictionary<string, List<string>> bound = null;

            if (_game != null)
            {
                if (!GameInstall.HasBepInEx(_game)) missing.Add("BepInEx");
                mod = GameInstall.InstalledModVersion(_game);
                if (mod == null) missing.Add("the mod");
                if (!File.Exists(Path.Combine(_game, "vdgs", "vdgs-shaders")))
                    missing.Add("the shader bundle");

                scenes = GameInstall.SceneDetails(_game);
                inGame = TracksInGame();
                bound = GameInstall.ReadBindings(_game);
                BuildTracks(scenes, inGame, bound, tracks, unbound);
            }

            Post(new
            {
                type = "state",
                game = _game,
                mod,
                missing,
                tracks,
                unbound,
                bundledMod = GameInstall.BundledModVersion(),
                busy = _busy,
                busyPercent = _busyPercent,
                catalog = CatalogState(scenes, mod, inGame, bound),
                stateMs = (int)clock.ElapsedMilliseconds,
                ready = _game != null && missing.Count == 0,
                running = _running = GameInstall.IsRunning(),
                launchArgs = GameInstall.LaunchArgs,
            });
        }

        /// <summary>
        /// The published list, marked up with what is already here. The capture is looked
        /// for by the folder the entry says it installs as, not by its name - two captures
        /// can be called the same thing and only the folder is the identity.
        /// </summary>
        private object CatalogState(List<GameInstall.SceneInfo> scenes,
                                    string mod,
                                    Dictionary<string, bool> inGame,
                                    Dictionary<string, List<string>> bound)
        {
            if (_catalog == null && _catalogError == null) return null;

            var entries = new List<object>();
            foreach (var e in _catalog ?? new List<Catalog.Entry>())
            {
                var haveCapture = e.InstallAs != null && scenes.Exists(s =>
                    string.Equals(s.Name, e.InstallAs, StringComparison.OrdinalIgnoreCase));
                entries.Add(new
                {
                    id = e.Id,
                    name = e.Name,
                    description = e.Description,
                    author = e.Author,
                    licence = e.Licence,
                    splats = e.Splats,
                    bytes = e.Bytes,
                    installed = haveCapture && e.TrackInPlace(inGame, bound),
                    // Said rather than merely enforced. A button that is off for a reason
                    // nobody can see is the same as a broken one.
                    needsMod = e.ModShortfall(mod),
                });
            }

            return new { url = _settings.CatalogUrl ?? Catalog.DefaultUrl, error = _catalogError, entries };
        }

        /// <summary>
        /// The tracks the game itself knows about, by name, each saying whether it came
        /// from the official server.
        ///
        /// Null means the database could not be read at all - most often because the game
        /// has never been run. Nothing is called missing or incomplete on that basis; not
        /// knowing and knowing it is absent are different answers.
        /// </summary>
        private static Dictionary<string, bool> TracksInGame()
        {
            try
            {
                var db = TrackStore.DatabasePath();
                if (!File.Exists(db)) return null;
                var map = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var t in TrackStore.List(db)) map[t.Name] = t.FromServer;
                return map;
            }
            catch { return null; }
        }

        /// <summary>
        /// Turns bindings into the list the window shows: one row per track the mod will
        /// put a capture on, plus whatever is installed that no track names.
        /// </summary>
        private void BuildTracks(List<GameInstall.SceneInfo> scenes,
                                 Dictionary<string, bool> inGame,
                                 Dictionary<string, List<string>> bound,
                                 List<object> tracks, List<object> unbound)
        {
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in bound)
            {
                long splats = 0, bytes = 0;
                var collision = kv.Value.Count > 0;
                var installed = kv.Value.Count > 0;
                var converted = true;

                foreach (var name in kv.Value)
                {
                    named.Add(name);
                    var info = scenes.Find(s =>
                        string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (info == null) { installed = false; continue; }
                    splats += info.Splats;
                    bytes += info.Bytes;
                    // One capture without a mesh is enough to fall through the floor.
                    collision &= info.Collision;
                    converted &= info.Converted;
                }

                tracks.Add(new
                {
                    track = kv.Key,
                    capture = kv.Value.Count > 0 ? string.Join(" + ", kv.Value.ToArray()) : null,
                    splats,
                    bytes,
                    collision,
                    captureInstalled = installed,
                    converted,
                    inGame = inGame == null || inGame.ContainsKey(kv.Key),
                    // A track from the official server can be unbound but not deleted.
                    fromServer = inGame != null && inGame.ContainsKey(kv.Key) && inGame[kv.Key],
                });
            }

            foreach (var s in scenes)
                if (!named.Contains(s.Name))
                    unbound.Add(new { name = s.Name, splats = s.Splats, collision = s.Collision, bytes = s.Bytes });
        }

        /// <summary>
        /// Runs one job off the UI thread, with the page told what is happening.
        ///
        /// Installing copies forty-odd files past a virus scanner and removing deletes
        /// them again; either takes seconds on a real machine. Doing that on the UI thread
        /// froze the window and swallowed the log until it was over, so the only thing a
        /// person could tell was that nothing had happened yet.
        /// </summary>
        private void RunBusy(string what, Action<Action<string>> job)
        {
            if (_busy != null) return;
            SetBusy(what);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = null;
                try { job(Log); }
                catch (Exception ex) { error = ex.Message; Log("failed: " + ex.Message); }

                OnUi(() =>
                {
                    _busy = null;
                    _busyPercent = null;
                    Push();
                    if (error != null)
                        MessageBox.Show(this, error, "VDGS",
                                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            });
        }

        // ------------------------------------------------------------------ page -> host

        private void Dispatch(string messageJson)
        {
            string cmd, id = null;
            try
            {
                using (var doc = JsonDocument.Parse(messageJson))
                {
                    cmd = doc.RootElement.TryGetProperty("cmd", out var v) ? v.GetString() : null;
                    if (doc.RootElement.TryGetProperty("id", out var i) &&
                        i.ValueKind == JsonValueKind.String) id = i.GetString();
                }
            }
            catch { return; }

            switch (cmd)
            {
                case "refresh": Push(); break;
                case "pick": PickGame(); break;
                case "installMod": InstallMod(); break;
                case "installCapture": InstallZip("Capture archive|vdgs-scene-*.zip|Zip archives|*.zip"); break;
                case "uninstallMod": UninstallMod(); break;
                case "refreshCatalog": RefreshCatalog(); break;
                case "get": GetFromCatalog(id); break;
                case "removeTrack": RemoveTrack(id); break;
                case "addTrack": AddTrack(); break;
                case "fly": Launch(); break;
            }
        }

        /// <summary>
        /// When the known locations miss, go looking - once, on the way in.
        ///
        /// The alternative is a window that says "not found" and leaves someone to find a
        /// folder they may not know the name of. It only runs when there is nothing, so it
        /// costs the common case nothing.
        /// </summary>
        private void FindGameIfMissing()
        {
            if (_game != null) return;
            RunBusy("looking for velocidrone", log =>
            {
                var found = GameInstall.ScanForGame(log);
                if (found == null) return;
                OnUi(() =>
                {
                    _game = found;
                    _settings.Game = found;
                    _settings.Save();
                });
            });
        }

        private void RefreshCatalog()
        {
            var url = _settings.CatalogUrl ?? Catalog.DefaultUrl;
            RunBusy("fetching the catalog", log =>
            {
                List<Catalog.Entry> got = null;
                string error = null;
                try
                {
                    got = Catalog.Fetch(url);
                    log("catalog: " + got.Count + " capture(s)");
                }
                catch (Exception ex)
                {
                    // Nothing published yet is the common case, and it is not a failure
                    // worth a dialog - the page says so where the list would be.
                    error = "could not read " + url + " - " + ex.Message;
                    log(error);
                }
                OnUi(() => { _catalog = got; _catalogError = error; });
            });
        }

        /// <summary>
        /// Downloads one entry and puts it where the mod will find it: the capture over the
        /// game folder, the track into the database, and the two bound together.
        ///
        /// All three, or it is not usable. A capture with no track is never reached in the
        /// game, and a track with no binding shows nothing when flown.
        /// </summary>
        private void GetFromCatalog(string id)
        {
            if (_game == null || id == null || _catalog == null) return;
            var entry = _catalog.Find(e => e.Id == id);
            if (entry == null) return;
            var game = _game;

            RunBusy("downloading " + entry.Name, log =>
            {
                var temp = Path.Combine(Path.GetTempPath(), "vdgs-download");

                // Everything that can refuse this, refusing before the download rather
                // than after it. A capture is hundreds of megabytes and several minutes,
                // and each of these is knowable up front - so what used to be a wasted
                // wait ending in an error is now a sentence.

                // The game holds both the capture folder and the track database open.
                // InstallArchive says so too, but only once the bytes are already spent,
                // and TrackStore.Import has no guard of its own - RemoveTrack and AddTrack
                // both check before writing that file, and this is the third writer.
                if (GameInstall.IsRunning())
                    throw new InvalidOperationException(
                        "VelociDrone is running. Close it first - files in use cannot be replaced.");

                // Refused, not merely greyed out. The button is drawn from a state that
                // can be a minute old, and the mod can be replaced while this window is
                // open; a capture whose renderer is not here yet fails as a wrong picture
                // rather than an error, which is the worst way to find out.
                var wants = entry.ModShortfall(GameInstall.InstalledModVersion(game));
                if (wants != null)
                    throw new InvalidOperationException(
                        entry.Name + " needs the mod from " + wants +
                        " or newer. Update it on the setup page first.");

                // The database not being there yet is the ordinary state of a machine the
                // game has never run on - which is exactly the machine this app is for.
                if (entry.Track != null && !File.Exists(TrackStore.DatabasePath()))
                    throw new FileNotFoundException(
                        "VelociDrone's database is not there yet - run the game once.",
                        TrackStore.DatabasePath());

                // The capture is fetched every time, including when a folder of that name
                // is already here. Skipping that leg was tried and taken back out: what it
                // could see was a readable meta.json, which an extraction cut short by a
                // closed window or a full disk also leaves behind - so a half-written
                // capture would have been called finished for good, with no way back to it
                // from inside the app. Overwriting is what repairs one. A hand-dropped
                // .ply of the same name fooled it the same way.
                var zip = Catalog.Download(entry.Scene, temp, p => Percent(p));
                try
                {
                    Percent(null);
                    SetBusy("installing " + entry.Name);
                    GameInstall.InstallArchive(game, zip, log, entry.InstallAs ?? entry.Name);
                }
                finally
                {
                    try { File.Delete(zip); } catch { }
                }

                if (entry.Track == null)
                {
                    log("no track published for this capture - bind it yourself once flying");
                    return;
                }

                var trackFile = Catalog.Download(entry.Track, temp, p => Percent(p));
                try
                {
                    Percent(null);
                    var t = Json.ParseTrackFile(File.ReadAllText(trackFile));

                    // The binding is written under the name the game will know it by, and
                    // the page looks for the name the catalog published. They agree only
                    // because make-catalog.sh copies one from the other; if they ever
                    // stop, the install works and reports as unfinished for good, each
                    // retry costing the whole capture again. So a catalog that disagrees
                    // with its own track file is refused rather than half-applied.
                    if (entry.TrackName != null && t.Name != entry.TrackName)
                        throw new InvalidOperationException(
                            "The catalog calls this track \"" + entry.TrackName +
                            "\" but the published file calls it \"" + t.Name +
                            "\". Nothing was changed.");
                    var db = TrackStore.DatabasePath();
                    if (!File.Exists(db))
                        throw new FileNotFoundException(
                            "VelociDrone's database is not there yet - run the game once.", db);

                    string backup;
                    switch (TrackStore.Import(db, t.Name, t.SceneId, t.Type, t.Value, out backup))
                    {
                        case TrackStore.ImportResult.Added:
                            log("added track \"" + t.Name + "\" (backup: " +
                                Path.GetFileName(backup) + ")");
                            break;
                        case TrackStore.ImportResult.AlreadyPresent:
                            log("track \"" + t.Name + "\" is already there, unchanged");
                            break;
                        case TrackStore.ImportResult.WouldOverwrite:
                            log("a different track is already called \"" + t.Name +
                                "\" - left alone, so yours is not replaced");
                            return;
                    }

                    if (entry.InstallAs != null)
                    {
                        GameInstall.Bind(game, t.Name, entry.InstallAs);
                        log("bound \"" + t.Name + "\" to " + entry.InstallAs);
                    }
                }
                finally
                {
                    try { File.Delete(trackFile); } catch { }
                }
            });
        }

        /// <summary>
        /// Says what is happening, and nothing else.
        ///
        /// This used to push a whole fresh state, which means walking every capture on
        /// disk before the word "installing" could reach the page - so the button looked
        /// dead for the couple of seconds that walk takes. Now the news goes first and the
        /// state catches up at the end.
        /// </summary>
        /// <summary>
        /// Takes a track off this machine: its binding always, and its row in the database
        /// when it is one we put there.
        ///
        /// The capture stays. It is the expensive half - gigabytes, and hours to fetch or
        /// build again - and nothing about dropping a course says it is unwanted.
        /// </summary>
        private void RemoveTrack(string name)
        {
            if (_game == null || name == null) return;

            var db = TrackStore.DatabasePath();
            TrackStore.Track row = null;
            try { if (File.Exists(db)) row = TrackStore.Find(db, name); } catch { }

            var mine = row != null && !row.FromServer;
            var question = mine
                ? "Remove the track \"" + name + "\" from VelociDrone?\n\n" +
                  "Its binding goes with it. The capture stays where it is, and the " +
                  "database is copied first."
                : "Stop showing a capture on \"" + name + "\"?\n\n" +
                  (row == null
                      ? "There is no such track in VelociDrone, so only the binding goes."
                      : "The track came from the official track server, so it is left alone - " +
                        "only the binding goes.");

            if (MessageBox.Show(this, question, "VDGS",
                                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            var game = _game;
            RunBusy("removing " + name, log =>
            {
                if (GameInstall.Unbind(game, name)) log("unbound \"" + name + "\"");

                if (!mine) return;
                if (GameInstall.IsRunning())
                    throw new InvalidOperationException(
                        "VelociDrone is running. Close it first - it keeps its track database open.");

                string backup;
                if (TrackStore.Remove(db, name, out backup))
                    log("removed track \"" + name + "\" (backup: " +
                        Path.GetFileName(backup) + ")");
                else
                    log("the track was already gone from the database");
            });
        }

        private void SetBusy(string what)
        {
            _busy = what;
            Post(new { type = "busy", what });
        }

        /// <summary>
        /// Progress is sent on its own rather than as a fresh state.
        ///
        /// A download reports a hundred times, and building the state means walking every
        /// capture on disk, reading each .ply header and opening the track database. A
        /// hundred of those during a download is a lot of disk for a number.
        /// </summary>
        private void Percent(int? p)
        {
            _busyPercent = p;
            Post(new { type = "progress", percent = p });
        }

        private void PickGame()
        {
            using (var d = new FolderBrowserDialog { Description = "Select the folder holding velocidrone.exe" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!GameInstall.IsGameFolder(d.SelectedPath))
                {
                    MessageBox.Show(this, "No velocidrone.exe in that folder.", "VDGS",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _game = d.SelectedPath;
                _settings.Game = _game;
                _settings.Save();
                Push();
            }
        }

        private void InstallMod()
        {
            if (_game == null) return;
            var game = _game;
            RunBusy("installing the mod", log =>
            {
                // The loader first if it is not there. Nobody presses this wanting the
                // plugin on its own - they want the mod to work, and it does not without
                // something to load it.
                if (!GameInstall.HasBepInEx(game)) BepInEx.Install(game, log);
                GameInstall.InstallBundledMod(game, log);
            });
        }

        private void UninstallMod()
        {
            if (_game == null) return;
            // The captures are the expensive part and none of them are touched, so the
            // question is worth asking once rather than leaving someone to wonder.
            var answer = MessageBox.Show(this,
                "Remove the mod from this VelociDrone?\n\n" +
                "The plugin, the shader bundle and the interface go. Your captures, " +
                "placements and track bindings all stay, and BepInEx is left alone.",
                "VDGS", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;

            var game = _game;
            RunBusy("removing the mod", log => GameInstall.UninstallMod(game, log));
        }

        private void InstallZip(string filter)
        {
            if (_game == null) return;
            using (var d = new OpenFileDialog { Filter = filter })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                // A capture archive is hundreds of megabytes; this is the slowest thing
                // the window does.
                var game = _game;
                var zip = d.FileName;
                RunBusy("installing " + Path.GetFileName(zip),
                        log => GameInstall.InstallArchive(game, zip, log));
            }
        }

        private void AddTrack()
        {
            if (_game == null) return;
            using (var d = new OpenFileDialog { Filter = "VDGS track|*.track.json|JSON|*.json" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    // The game holds user11.db open, so this is only safe while it is closed
                    // - which is the reason installing and launching live in the same tool.
                    if (GameInstall.IsRunning())
                        throw new InvalidOperationException(
                            "Close VelociDrone first - it keeps its track database open.");

                    var t = Json.ParseTrackFile(File.ReadAllText(d.FileName));
                    var db = TrackStore.DatabasePath();
                    if (!File.Exists(db))
                        throw new FileNotFoundException(
                            "VelociDrone's database is not there yet - run the game once.", db);

                    string backup;
                    var r = TrackStore.Import(db, t.Name, t.SceneId, t.Type, t.Value, out backup);
                    switch (r)
                    {
                        case TrackStore.ImportResult.Added:
                            Log("added track \"" + t.Name + "\" (backup: " + Path.GetFileName(backup) + ")");
                            BindIfObvious(t.Name);
                            break;
                        case TrackStore.ImportResult.AlreadyPresent:
                            Log("track \"" + t.Name + "\" is already there, unchanged");
                            BindIfObvious(t.Name);
                            break;
                        case TrackStore.ImportResult.WouldOverwrite:
                            Log("a different track is already called \"" + t.Name + "\" - left alone");
                            MessageBox.Show(this,
                                "You already have a track called \"" + t.Name + "\" and its layout " +
                                "differs from this one.\n\nIt has been left as it is. Rename or " +
                                "delete yours in the game if you want this version.",
                                "VDGS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log("failed: " + ex.Message);
                    MessageBox.Show(this, ex.Message, "VDGS", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>
        /// A track shows a capture only once the two are bound by name. With exactly one
        /// capture installed there is nothing to choose, so it is bound rather than left as
        /// a step to discover; with several, the browser UI is where that choice belongs.
        /// </summary>
        private void BindIfObvious(string trackName)
        {
            var scenes = GameInstall.InstalledScenes(_game).ToList();
            if (scenes.Count != 1)
            {
                Log("bind \"" + trackName + "\" to a capture at http://localhost:8777/ once flying");
                return;
            }
            GameInstall.Bind(_game, trackName, scenes[0]);
            Log("bound \"" + trackName + "\" to " + scenes[0]);
        }

        private void Launch()
        {
            try
            {
                if (GameInstall.IsRunning())
                {
                    Log("VelociDrone is already running");
                    return;
                }
                GameInstall.Launch(_game);
                Log("started with " + GameInstall.LaunchArgs);
                Push();
            }
            catch (Exception ex)
            {
                Log("failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "VDGS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
