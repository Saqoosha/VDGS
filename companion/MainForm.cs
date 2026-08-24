using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VDGSCompanion
{
    /// <summary>
    /// One window: what is installed, what is missing, and a button that fixes it.
    ///
    /// The audience is someone who wants to fly, not to read a setup guide, so state is
    /// shown rather than assumed - a player who cannot tell whether the mod is installed
    /// ends up reinstalling over a working setup.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private string _game;
        private readonly Label _gameLabel = new Label();
        private readonly Label _status = new Label();
        private readonly ListBox _scenes = new ListBox();
        private readonly TextBox _log = new TextBox();
        private readonly Button _launch = new Button();

        internal MainForm()
        {
            Text = "VDGS Companion";
            Size = new Size(720, 560);
            MinimumSize = new Size(620, 480);
            Font = SystemFonts.MessageBoxFont;

            var pick = new Button { Text = "Change…", Left = 560, Top = 12, Width = 120 };
            pick.Click += (s, e) => PickGame();
            _gameLabel.SetBounds(14, 16, 540, 20);
            _gameLabel.AutoEllipsis = true;

            _status.SetBounds(14, 44, 666, 44);

            var scenesLabel = new Label { Text = "Captures installed", Left = 14, Top = 96, Width = 300 };
            _scenes.SetBounds(14, 118, 400, 160);

            var addMod = new Button { Text = "Install mod (.zip)…", Left = 428, Top = 118, Width = 252, Height = 30 };
            var addScene = new Button { Text = "Install capture (.zip)…", Left = 428, Top = 156, Width = 252, Height = 30 };
            var addTrack = new Button { Text = "Add track (.track.json)…", Left = 428, Top = 194, Width = 252, Height = 30 };
            addMod.Click += (s, e) => InstallZip("Mod archive|vdgs-mod-*.zip|Zip archives|*.zip");
            addScene.Click += (s, e) => InstallZip("Capture archive|vdgs-scene-*.zip|Zip archives|*.zip");
            addTrack.Click += (s, e) => AddTrack();

            _launch.SetBounds(428, 240, 252, 38);
            _launch.Text = "Fly";
            _launch.Click += (s, e) => Launch();

            var logLabel = new Label { Text = "Log", Left = 14, Top = 292, Width = 100 };
            _log.SetBounds(14, 314, 666, 190);
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = SystemColors.Window;

            Controls.AddRange(new Control[] { _gameLabel, pick, _status, scenesLabel, _scenes,
                                              addMod, addScene, addTrack, _launch, logLabel, _log });

            _game = GameInstall.FindGame();
            Refresh_();
        }

        private void Log(string line)
        {
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine);
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
                Refresh_();
            }
        }

        private void Refresh_()
        {
            _scenes.Items.Clear();

            if (_game == null)
            {
                _gameLabel.Text = "VelociDrone not found";
                _status.Text = "Use Change… to point at the folder that holds velocidrone.exe.";
                _status.ForeColor = Color.Firebrick;
                _launch.Enabled = false;
                return;
            }

            _gameLabel.Text = _game;
            foreach (var s in GameInstall.InstalledScenes(_game)) _scenes.Items.Add(s);

            var bep = GameInstall.HasBepInEx(_game);
            var mod = GameInstall.InstalledModVersion(_game);
            var shaders = File.Exists(Path.Combine(_game, "vdgs", "vdgs-shaders"));

            var missing = new System.Collections.Generic.List<string>();
            if (!bep) missing.Add("BepInEx");
            if (mod == null) missing.Add("the mod");
            if (!shaders) missing.Add("the shader bundle");

            if (missing.Count > 0)
            {
                _status.Text = "Missing: " + string.Join(", ", missing) +
                    (bep ? "" : "\r\nBepInEx 5.4.23.5 (win_x64) has to be unzipped into the game folder first.");
                _status.ForeColor = Color.Firebrick;
            }
            else
            {
                _status.Text = "Mod " + mod + " installed, " + _scenes.Items.Count + " capture(s) ready."
                             + "\r\nFly starts the game with " + GameInstall.LaunchArgs +
                               ", which the captures need in order to draw at all.";
                _status.ForeColor = Color.DarkGreen;
            }
            _launch.Enabled = true;
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
                    Refresh_();
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
            }
            catch (Exception ex)
            {
                Log("failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "VDGS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
