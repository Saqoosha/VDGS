using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// The window is a WebView2 showing AppUi.Html, which is the same design as the mod's
    /// in-game browser UI. The file and database work stays in C# where it is tested; the
    /// page only sends command names back and renders the state it is given.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly WebView2 _web = new WebView2 { Dock = DockStyle.Fill };
        private readonly JsonSerializerOptions _json =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private string _game;
        private bool _ready;   // the page is up and can be posted to

        internal MainForm()
        {
            Text = "VDGS Companion";
            Size = new Size(780, 860);
            MinimumSize = new Size(640, 620);
            BackColor = Color.FromArgb(0xf6, 0xf7, 0xf9);   // matches the page, so no white flash
            Controls.Add(_web);

            _game = GameInstall.FindGame();
            Load += async (s, e) => await Start();
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
            c.NavigationCompleted += (s, e) => { _ready = true; Push(); };
            c.NavigateToString(AppUi.Html);
        }

        // ------------------------------------------------------------------ host -> page

        private void Post(object payload)
        {
            if (_ready) _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, _json));
        }

        private void Log(string line) =>
            Post(new { type = "log", line = DateTime.Now.ToString("HH:mm:ss") + "  " + line });

        private void Push()
        {
            var missing = new List<string>();
            var scenes = new List<object>();
            string mod = null;

            if (_game != null)
            {
                if (!GameInstall.HasBepInEx(_game)) missing.Add("BepInEx");
                mod = GameInstall.InstalledModVersion(_game);
                if (mod == null) missing.Add("the mod");
                if (!File.Exists(Path.Combine(_game, "vdgs", "vdgs-shaders")))
                    missing.Add("the shader bundle");

                foreach (var s in GameInstall.SceneDetails(_game))
                    scenes.Add(new { name = s.Name, splats = s.Splats.ToString("N0"), collision = s.Collision });
            }

            Post(new
            {
                type = "state",
                game = _game,
                mod,
                missing,
                scenes,
                ready = _game != null && missing.Count == 0,
                running = GameInstall.IsRunning(),
                launchArgs = GameInstall.LaunchArgs,
            });
        }

        // ------------------------------------------------------------------ page -> host

        private void Dispatch(string messageJson)
        {
            string cmd;
            try
            {
                using (var doc = JsonDocument.Parse(messageJson))
                    cmd = doc.RootElement.TryGetProperty("cmd", out var v) ? v.GetString() : null;
            }
            catch { return; }

            switch (cmd)
            {
                case "refresh": Push(); break;
                case "pick": PickGame(); break;
                case "installMod": InstallZip("Mod archive|vdgs-mod-*.zip|Zip archives|*.zip"); break;
                case "installScene": InstallZip("Capture archive|vdgs-scene-*.zip|Zip archives|*.zip"); break;
                case "addTrack": AddTrack(); break;
                case "fly": Launch(); break;
            }
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
                Push();
            }
        }

        private void InstallZip(string filter)
        {
            if (_game == null) return;
            using (var d = new OpenFileDialog { Filter = filter })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    GameInstall.InstallArchive(_game, d.FileName, Log);
                    Push();
                }
                catch (Exception ex)
                {
                    Log("failed: " + ex.Message);
                    MessageBox.Show(this, ex.Message, "VDGS", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
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
