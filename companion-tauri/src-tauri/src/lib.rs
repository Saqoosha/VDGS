//! Tauri host: dispatch, busy jobs, dialogs, and the running watcher.

pub mod bepinex;
pub mod catalog;
pub mod cli;
pub mod game;
pub mod launch;
pub mod settings;
pub mod state;
pub mod tracks;

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use serde::Serialize;
use serde_json::json;
use settings::Settings;
use tauri::{Emitter, Manager};
use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};

pub struct Host {
    app: tauri::AppHandle,
    resource_dir: PathBuf,
    inner: Mutex<Inner>,
}

struct Inner {
    settings: Settings,
    game: Option<PathBuf>,
    catalog: Option<Vec<catalog::Entry>>,
    catalog_error: Option<String>,
    /// A companion build newer than this one, as the catalog offers it. The mod ships inside
    /// the app, so an app nobody updates keeps installing the old mod: the R6 sky of
    /// 2026-09-23 needed a new companion, and the old one had no way to say so.
    app_update: Option<String>,
    busy: Option<String>,
    busy_percent: Option<u8>,
    running: bool,
    child: Option<std::process::Child>,
}

impl Host {
    fn post<T: Serialize>(&self, payload: T) {
        // Emitter::emit wants Clone; SetupState does not implement it, so go via Value.
        if let Ok(value) = serde_json::to_value(payload) {
            let _ = self.app.emit("push", value);
        }
    }

    fn log(&self, line: &str) {
        self.post(json!({"type":"log","line": format!("{}  {}", now_hms(), line)}));
    }

    fn set_busy(&self, what: Option<&str>) {
        self.inner.lock().unwrap().busy = what.map(String::from);
        self.post(json!({"type":"busy","what": what}));
    }

    fn percent(&self, p: Option<u8>) {
        self.inner.lock().unwrap().busy_percent = p;
        self.post(json!({"type":"progress","percent": p}));
    }

    fn push(&self) {
        let running = launch::is_running();
        let snapshot = {
            let mut i = self.inner.lock().unwrap();
            i.running = running;
            Snapshot {
                game: i.game.clone(),
                catalog: i.catalog.clone(),
                catalog_error: i.catalog_error.clone(),
                app_update: i.app_update.clone(),
                catalog_url: i
                    .settings
                    .catalog_url
                    .clone()
                    .unwrap_or_else(|| catalog::DEFAULT_URL.to_string()),
                busy: i.busy.clone(),
                busy_percent: i.busy_percent,
            }
        };
        let built = state::build(state::Inputs {
            game: snapshot.game.as_deref(),
            resource_dir: &self.resource_dir,
            catalog: snapshot.catalog.as_deref(),
            catalog_error: snapshot.catalog_error.as_deref(),
            app_update: snapshot.app_update.as_deref(),
            catalog_url: &snapshot.catalog_url,
            busy: snapshot.busy.as_deref(),
            busy_percent: snapshot.busy_percent,
            running,
        });
        self.post(built);
    }

    fn run_busy(
        self: &Arc<Self>,
        what: &str,
        job: impl FnOnce(&Host, &mut dyn FnMut(String)) -> Result<(), String> + Send + 'static,
    ) {
        {
            let mut i = self.inner.lock().unwrap();
            if let Some(running) = i.busy.as_ref() {
                let msg = format!("already busy - {running} is running");
                drop(i);
                self.log(&msg);
                return;
            }
            i.busy = Some(what.to_string());
        }
        self.post(json!({"type":"busy","what": what}));
        let me = Arc::clone(self);
        std::thread::spawn(move || {
            let mut log = |s: String| me.log(&s);
            let err = job(&me, &mut log).err();
            if let Some(e) = &err {
                me.log(&format!("failed: {e}"));
            }
            {
                let mut i = me.inner.lock().unwrap();
                i.busy = None;
                i.busy_percent = None;
            }
            me.push();
            if let Some(e) = err {
                me.error_dialog(&e);
            }
        });
    }

    fn error_dialog(&self, msg: &str) {
        self.app
            .dialog()
            .message(msg)
            .title("VDGS")
            .kind(MessageDialogKind::Error)
            .blocking_show();
    }

    fn warn_dialog(&self, msg: &str) {
        self.app
            .dialog()
            .message(msg)
            .title("VDGS")
            .kind(MessageDialogKind::Warning)
            .blocking_show();
    }

    fn confirm(&self, msg: &str) -> bool {
        self.app
            .dialog()
            .message(msg)
            .title("VDGS")
            .buttons(MessageDialogButtons::OkCancel)
            .blocking_show()
    }

    fn pick_game(&self) {
        let mut picker = self.app.dialog().file();
        #[cfg(windows)]
        {
            picker = picker.set_title("Select the folder holding velocidrone.exe");
        }
        #[cfg(target_os = "macos")]
        {
            picker = picker.set_title("Select the folder holding velocidrone.app");
        }
        let Some(picked) = picker.blocking_pick_folder() else {
            return;
        };
        let Ok(path) = picked.into_path() else {
            return;
        };
        let Some(app) = resolve_picked_game(path) else {
            #[cfg(target_os = "macos")]
            self.warn_dialog("No velocidrone.app in that folder.");
            #[cfg(windows)]
            self.warn_dialog("No velocidrone.exe in that folder.");
            return;
        };
        {
            let mut i = self.inner.lock().unwrap();
            i.settings.game = Some(app.to_string_lossy().into_owned());
            i.settings.save();
            i.game = Some(app);
        }
        self.push();
    }

    /// Finds the game, after the page can already be talked to.
    ///
    /// Runs on a thread rather than in `setup` because both halves can be slow and neither
    /// is worth an empty window: the guesses stat every drive letter on Windows, and the
    /// walk below that reads the disk. `setup` keeps only the remembered path, which is one
    /// stat, so the state is managed and answerable within microseconds either way.
    ///
    /// The guesses are silent — on a machine where one hits they take no time and a busy
    /// label would only flicker. The walk is not, and says so.
    fn locate_game(self: &Arc<Self>) {
        if self.inner.lock().unwrap().game.is_some() {
            return;
        }
        if let Some(found) = game::find() {
            let mut i = self.inner.lock().unwrap();
            // Re-read under the lock for the reason spelled out on the walk below: the
            // folder picker is live the whole time this runs.
            if i.game.is_none() {
                i.settings.game = Some(found.to_string_lossy().into_owned());
                i.settings.save();
                i.game = Some(found);
            }
            drop(i);
            self.push();
            return;
        }
        #[cfg(windows)]
        self.find_game_if_missing();
        #[cfg(not(windows))]
        self.push();
    }

    /// Walks the disk for the game, but only when the guesses found nothing.
    ///
    /// Windows only, and separate from `game::find` on purpose: the guesses run before the
    /// page can draw anything and must stay cheap, while this one can take a while. The
    /// busy label carries it, because that lives in `Inner` and the page's first `refresh`
    /// reads it back. The log lines do not: this is called from `setup`, and a `post` made
    /// before the page subscribes is dropped — the same `listen()` race the bridge gates
    /// against. `MainForm.cs` calls its equivalent from `NavigationCompleted` instead, so
    /// matching that is the fix; until then, do not read the log for evidence the walk ran.
    /// Finding nothing is not a failure — plenty of people keep the game somewhere this
    /// walk does not reach, and the folder picker is still there.
    #[cfg(windows)]
    fn find_game_if_missing(self: &Arc<Self>) {
        if self.inner.lock().unwrap().game.is_some() {
            return;
        }
        self.run_busy("looking for velocidrone", |host, log| {
            let Some(found) = game::scan_for_game(log) else {
                return Ok(());
            };
            let mut i = host.inner.lock().unwrap();
            // Checked again, under the lock, because the walk takes minutes and the folder
            // picker stays live throughout it - `pick_game` does not go through `run_busy`,
            // and its button is the one button on the page without `disabled={busy}`.
            // Someone who gets tired of waiting and points at their game by hand had that
            // choice overwritten by whichever velocidrone.exe the walk reached first, and
            // every launch afterwards ran the wrong one.
            if i.game.is_some() {
                log("kept the folder you picked".into());
                return Ok(());
            }
            i.settings.game = Some(found.to_string_lossy().into_owned());
            i.settings.save();
            i.game = Some(found);
            Ok(())
        });
    }

    fn install_mod(self: &Arc<Self>) {
        let Some(app) = self.inner.lock().unwrap().game.clone() else {
            return;
        };
        let resource_dir = self.resource_dir.clone();
        self.run_busy("installing the mod", move |host, log| {
            if launch::is_running() {
                return Err(
                    "VelociDrone is running. Close it first - files in use cannot be replaced."
                        .into(),
                );
            }
            let root = game::root(&app);
            // Not "is a loader here" but "is it ours": an official BepInEx has the same
            // files and a preloader that dies on arm64.
            if !bepinex::is_ours(&root) {
                bepinex::install(&root, log, &mut |p| host.percent(Some(p)))
                    .map_err(|e| e.to_string())?;
                host.percent(None);
            }
            let Some(bundled) = game::bundled_mod_dir(&resource_dir) else {
                return Err("This build carries no mod payload.".into());
            };
            game::install_bundled_mod(&root, &bundled, log).map_err(|e| e.to_string())
        });
    }

    fn uninstall_mod(self: &Arc<Self>) {
        let Some(app) = self.inner.lock().unwrap().game.clone() else {
            return;
        };
        let ok = self.confirm(
            "Remove the mod from this VelociDrone?\n\n\
             The plugin, the shader bundle and the interface go. Your captures, \
             placements and track bindings all stay, and BepInEx is left alone.",
        );
        if !ok {
            return;
        }
        self.run_busy("removing the mod", move |_host, log| {
            if launch::is_running() {
                return Err(
                    "VelociDrone is running. Close it first - files in use cannot be replaced."
                        .into(),
                );
            }
            let root = game::root(&app);
            game::uninstall_mod(&root, log).map_err(|e| e.to_string())
        });
    }

    /// Picks a .ply and hands its path back to the page - nothing is copied yet.
    ///
    /// Adding a track is file -> name -> create, and the copy waits for the name: a
    /// capture that lands in `<game>/vdgs/` before the person has committed to a track
    /// is the "installed, on no track" state the earlier layout kept producing (cancel
    /// at the name step and the file stayed). The page opens the name dialog on `picked`
    /// and sends `addTrack {path, name}` when it is confirmed; cancel sends nothing.
    fn pick_ply(self: &Arc<Self>) {
        if self.inner.lock().unwrap().game.is_none() {
            return;
        }
        if launch::is_running() {
            self.error_dialog(
                "VelociDrone is running. Close it first - the track database is in use.",
            );
            return;
        }
        let Some(picked) = self
            .app
            .dialog()
            .file()
            .add_filter("Gaussian splat capture", &["ply"])
            .blocking_pick_file()
        else {
            return;
        };
        let Ok(ply) = picked.into_path() else {
            return;
        };
        let stem = ply
            .file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or("capture")
            .to_string();
        self.post(json!({"type":"picked","path": ply.to_string_lossy(),"stem": stem}));
    }

    /// Opens the download page in the system browser. The page sends no URL: the one opened
    /// is the origin of the catalog this app already reads, and only while an update is on
    /// offer, so nothing the page says can make this open anything else.
    fn open_app_update(&self) {
        let url = {
            let i = self.inner.lock().unwrap();
            if i.app_update.is_none() {
                return;
            }
            let catalog = i
                .settings
                .catalog_url
                .clone()
                .unwrap_or_else(|| catalog::DEFAULT_URL.to_string());
            match catalog::site_of(&catalog) {
                Some(site) => site,
                None => return,
            }
        };
        #[cfg(target_os = "macos")]
        let opened = std::process::Command::new("open").arg(&url).spawn();
        // explorer.exe hands a URL to the default browser without a shell in between, so
        // nothing in it is parsed as a command. Its exit status is meaningless (1 on success).
        #[cfg(windows)]
        let opened = std::process::Command::new("explorer").arg(&url).spawn();
        #[cfg(not(any(target_os = "macos", windows)))]
        let opened: std::io::Result<std::process::Child> =
            Err(std::io::Error::other("no browser opener on this platform"));
        if let Err(e) = opened {
            self.log(&format!("could not open {url}: {e}"));
        }
    }

    fn refresh_catalog(self: &Arc<Self>) {
        let url = self
            .inner
            .lock()
            .unwrap()
            .settings
            .catalog_url
            .clone()
            .unwrap_or_else(|| catalog::DEFAULT_URL.to_string());
        self.run_busy("fetching the catalog", move |host, log| {
            match catalog::fetch(&url) {
                Ok(got) => {
                    log(format!("catalog: {} capture(s)", got.entries.len()));
                    let own = host.app.package_info().version.to_string();
                    let update = catalog::newer_app(got.app_version.as_deref(), &own);
                    if let Some(v) = &update {
                        log(format!("VDGS {v} is out - this app is {own}"));
                    }
                    let mut i = host.inner.lock().unwrap();
                    i.catalog = Some(got.entries);
                    i.catalog_error = None;
                    i.app_update = update;
                    Ok(())
                }
                Err(ex) => {
                    let error = format!("could not read {url} - {ex}");
                    log(error.clone());
                    let mut i = host.inner.lock().unwrap();
                    i.catalog = None;
                    i.catalog_error = Some(error);
                    Ok(())
                }
            }
        });
    }

    /// `replace`: the track is bound to some other capture and is to be rebound to this
    /// entry's, even when the entry's folder is already on disk - which a plain get treats
    /// as an update and so leaves the binding alone.
    fn get_from_catalog(self: &Arc<Self>, id: &str, replace: bool) {
        let (app, entry) = {
            let i = self.inner.lock().unwrap();
            let Some(app) = i.game.clone() else {
                return;
            };
            let Some(catalog) = i.catalog.as_ref() else {
                return;
            };
            let Some(entry) = catalog.iter().find(|e| e.id == id).cloned() else {
                return;
            };
            (app, entry)
        };

        let what = format!("downloading {}", entry.name);
        self.run_busy(&what, move |host, log| {
            let temp = std::env::temp_dir().join("vdgs-download");
            let root = game::root(&app);

            if launch::is_running() {
                return Err(
                    "VelociDrone is running. Close it first - files in use cannot be replaced."
                        .into(),
                );
            }

            if entry.track.is_some() {
                let db0 = tracks::db_path();
                if !db0.is_file() {
                    return Err(format!(
                        "VelociDrone's database is not there yet - run the game once. ({})",
                        db0.display()
                    ));
                }
                if let Err(ex) = tracks::list(&db0) {
                    return Err(format!(
                        "VelociDrone's track database is there but could not be read: {ex}"
                    ));
                }
            }

            let mut track_file: Option<PathBuf> = None;
            if let Some(track) = &entry.track {
                let path = catalog::download(track, &temp, &mut |p| host.percent(Some(p)))
                    .map_err(|e| e.to_string())?;
                host.percent(None);
                track_file = Some(path);
            }

            let result = (|| -> Result<(), String> {
                let mut parsed: Option<tracks::TrackFile> = None;
                if let Some(ref tf) = track_file {
                    let text = std::fs::read_to_string(tf).map_err(|e| e.to_string())?;
                    let t: tracks::TrackFile =
                        serde_json::from_str(&text).map_err(|e| e.to_string())?;
                    if let Some(ref published) = entry.track_name {
                        if tracks::display_name(&t.name) != tracks::display_name(published) {
                            return Err(format!(
                                "The catalog calls this track \"{published}\" but the published file calls it \"{}\". Nothing was changed.",
                                t.name
                            ));
                        }
                    }
                    // Checked before the capture download, not after: rebinding to a
                    // track the database holds with other gates would fly this capture
                    // over another course, and that is worth refusing before hundreds
                    // of megabytes rather than after.
                    if replace {
                        let db = tracks::db_path();
                        if let Some(existing) = tracks::find(&db, &t.name).map_err(|e| e.to_string())? {
                            if existing.value != t.value_string() {
                                return Err(format!(
                                    "A different track is already called \"{}\" in VelociDrone, with other gates. Replace would put this capture over another course, so nothing was changed.",
                                    tracks::display_name(&t.name)
                                ));
                            }
                        }
                    }
                    parsed = Some(t);
                }

                let mut updating = false;
                let zip = catalog::download(&entry.scene, &temp, &mut |p| host.percent(Some(p)))
                    .map_err(|e| e.to_string())?;
                let install_result = (|| {
                    host.percent(None);
                    host.set_busy(Some(&format!("installing {}", entry.name)));
                    match entry.install_as.as_deref() {
                        Some(install_as) => {
                            game::install_capture_archive(&root, &zip, install_as, log)
                                .map(|replaced| updating = replaced)
                        }
                        None => game::install_archive(&root, &zip, &entry.name, log),
                    }
                    .map_err(|e| e.to_string())
                })();
                let _ = std::fs::remove_file(&zip);
                install_result?;

                let Some(t) = parsed else {
                    log(
                        "no track published for this capture - bind it yourself once flying"
                            .into(),
                    );
                    return Ok(());
                };

                let db = tracks::db_path();
                let value = t.value_string();
                if launch::is_running() {
                    return Err(
                        "VelociDrone is running. Close it first - it keeps its track database open."
                            .into(),
                    );
                }
                let (result, backup) =
                    tracks::import(&db, &t.name, t.scene_id, t.kind, &value)
                        .map_err(|e| e.to_string())?;
                match result {
                    tracks::ImportResult::Added => {
                        let backup_name = backup
                            .as_ref()
                            .and_then(|p| p.file_name())
                            .and_then(|s| s.to_str())
                            .unwrap_or("?");
                        log(format!(
                            "added track \"{}\" (backup: {backup_name})",
                            t.name
                        ));
                    }
                    tracks::ImportResult::AlreadyPresent => {
                        log(format!(
                            "track \"{}\" is already there, unchanged",
                            t.name
                        ));
                    }
                    tracks::ImportResult::WouldOverwrite => {
                        log(format!(
                            "a different track is already called \"{}\" - left alone, so yours is not replaced",
                            t.name
                        ));
                        return Ok(());
                    }
                }

                if let Some(ref install_as) = entry.install_as {
                    let shown = tracks::display_name(&t.name);
                    // An update keeps whatever binding the track has; a replace is the
                    // request to change it.
                    if game::bind(&root, &shown, install_as, updating && !replace)
                        .map_err(|e| e.to_string())?
                    {
                        log(format!("bound \"{shown}\" to {install_as}"));
                    } else {
                        log(format!("\"{shown}\" keeps its binding"));
                    }
                }
                Ok(())
            })();

            if let Some(tf) = track_file {
                let _ = std::fs::remove_file(tf);
            }
            result
        });
    }

    /// Removing a track now removes its bound capture too - not just the binding.
    ///
    /// The old design kept the capture on the theory that it is hundreds of megabytes and
    /// a track is a small click, so you might want to rebind the same capture to a
    /// different track later. That reasoning only holds if the person can tell what is
    /// left behind and why - and they could not. The first real user to hit this in
    /// testing removed two tracks and found both captures still listed as installed -
    /// "confusing, so users will confuse."
    fn remove_track(self: &Arc<Self>, name: &str) {
        let (app, catalog) = {
            let inner = self.inner.lock().unwrap();
            let Some(app) = inner.game.clone() else {
                return;
            };
            (app, inner.catalog.clone())
        };
        let db = tracks::db_path();
        let row = if db.is_file() {
            tracks::find(&db, name).ok().flatten()
        } else {
            None
        };
        let mine = row.as_ref().is_some_and(|t| !t.from_server);

        // Read the bound captures now, before `unbind` erases this exact mapping - by the
        // time the job below runs there is nothing left to say which captures this track
        // was showing. A track can be bound to more than one (`bindings.json` maps a name
        // to a list), so this is a list even though the common case holds one.
        let root = game::root(&app);
        let all_bindings = game::try_read_bindings(&root).unwrap_or_default();
        let captures = all_bindings.get(name).cloned().unwrap_or_default();

        // Binding the same capture to a second track is ordinary - `game::bind` never
        // refuses it. So a capture this track is bound to may
        // also be named under some other key in the same map, and deleting it here would
        // silently break that other track's binding without telling anyone. Split the list
        // now, purely to word the dialog correctly; `remove_track_job` re-derives the same
        // question against a fresh read right before it would actually delete anything,
        // because this snapshot can go stale while the confirmation sits on screen.
        let (to_delete, kept_shared): (Vec<String>, Vec<String>) = captures
            .iter()
            .cloned()
            .partition(|c| !capture_used_elsewhere(&all_bindings, name, c));

        let capture_sentence =
            capture_removal_sentence(&to_delete, &kept_shared, catalog.as_deref());
        let extra = capture_sentence
            .map(|s| format!(" {s}"))
            .unwrap_or_default();

        let question = if mine {
            format!(
                "Remove the track \"{name}\" from VelociDrone?\n\n\
                 Its binding goes with it.{extra} The database is copied first."
            )
        } else if row.is_none() {
            if extra.is_empty() {
                format!(
                    "Stop showing a capture on \"{name}\"?\n\n\
                     There is no such track in VelociDrone, so only the binding goes."
                )
            } else {
                format!(
                    "Stop showing a capture on \"{name}\"?\n\n\
                     There is no such track in VelociDrone, so the binding goes.{extra}"
                )
            }
        } else if extra.is_empty() {
            format!(
                "Stop showing a capture on \"{name}\"?\n\n\
                 The track came from the official track server, so it is left alone - \
                 only the binding goes."
            )
        } else {
            format!(
                "Stop showing a capture on \"{name}\"?\n\n\
                 The track came from the official track server, so it is left alone - \
                 the binding goes.{extra}"
            )
        };
        if !self.confirm(&question) {
            return;
        }
        let name = name.to_string();
        self.run_busy(&format!("removing {name}"), move |_host, log| {
            remove_track_job(&root, &db, &name, mine, &captures, log)
        });
    }

    fn unbind_track(self: &Arc<Self>, track: &str) {
        let Some(app) = self.inner.lock().unwrap().game.clone() else {
            return;
        };
        let track = track.to_string();
        self.run_busy(&format!("unbinding {track}"), move |_host, log| {
            // No is_running guard: bindings.json is ours, not the game's, and the plugin
            // picks the change up from its own poll within a second.
            let root = game::root(&app);
            if game::unbind(&root, &track).map_err(|e| e.to_string())? {
                log(format!("unbound \"{track}\""));
            } else {
                log(format!("\"{track}\" was not bound"));
            }
            Ok(())
        });
    }

    fn remove_capture(self: &Arc<Self>, name: &str) {
        let (app, catalog) = {
            let inner = self.inner.lock().unwrap();
            let Some(app) = inner.game.clone() else {
                return;
            };
            (app, inner.catalog.clone())
        };
        // The same question Remove asks for a track: this is hundreds of megabytes gone
        // on one click, and whether it can come back depends on the catalog.
        let fetchable = capture_is_fetchable(name, catalog.as_deref());
        let question = if fetchable {
            format!("Remove the capture \"{name}\"? It can be fetched again from the catalog.")
        } else {
            format!(
                "Remove the capture \"{name}\"? Without the original .ply it cannot be recovered."
            )
        };
        if !self.confirm(&question) {
            return;
        }
        let name = name.to_string();
        self.run_busy(&format!("removing {name}"), move |_host, log| {
            if launch::is_running() {
                return Err(
                    "VelociDrone is running. Close it first - files in use cannot be removed."
                        .into(),
                );
            }
            let root = game::root(&app);
            if game::remove_capture(&root, &name).map_err(|e| e.to_string())? {
                log(format!("removed {name}"));
            } else {
                log(format!("{name} was not there"));
            }
            Ok(())
        });
    }

    /// Copies the picked .ply in and creates a track bound to it, as one job.
    ///
    /// The two happen together on purpose. Binding is keyed by track name, so a capture
    /// installed now and bound later is a rename waiting to break the link - and a broken
    /// link shows nothing at all, with no error anywhere. The rest of the logic lives in
    /// the free function [`add_track_job`].
    fn add_track(self: &Arc<Self>, path: &str, name: &str) {
        let Some(app) = self.inner.lock().unwrap().game.clone() else {
            return;
        };
        let display = name.trim().to_string();
        if display.is_empty() {
            self.error_dialog("a track needs a name");
            return;
        }
        let ply = PathBuf::from(path);
        let resource_dir = self.resource_dir.clone();
        let what = format!("adding {display}");
        self.run_busy(&what, move |_host, log| {
            if launch::is_running() {
                return Err(
                    "VelociDrone is running. Close it first - the track database is in use."
                        .into(),
                );
            }
            let db = tracks::db_path();
            let root = game::root(&app);
            add_track_job(&resource_dir, &db, &root, &ply, &display, log)
        });
    }

    fn launch(&self) {
        let Some(app) = self.inner.lock().unwrap().game.clone() else {
            return;
        };
        if launch::is_running() {
            self.log("VelociDrone is already running");
            return;
        }
        match launch::spawn(&app) {
            Ok(child) => {
                self.inner.lock().unwrap().child = Some(child);
                self.log("started through Doorstop");
                self.push();
            }
            Err(ex) => {
                self.log(&format!("failed: {ex}"));
                self.error_dialog(&ex.to_string());
            }
        }
    }
}

/// One job for Add track: the capture lands in `<game>/vdgs/` and the track is created
/// and bound to it, in that order. Copying first means a failure at the database step
/// leaves a capture with no track - visible in the "installed, on no track" line and
/// removable from there - rather than a track whose capture never arrived, which the
/// game would show as nothing with no error.
fn add_track_job(
    resource_dir: &Path,
    db: &Path,
    root: &Path,
    ply: &Path,
    display: &str,
    log: &mut dyn FnMut(String),
) -> Result<(), String> {
    let capture = game::install_ply(root, ply).map_err(|e| e.to_string())?;
    log(format!("installed {capture}"));
    create_track_job(resource_dir, db, root, display, &capture, log)
}

/// The track half of [`add_track_job`], pulled out so it can be exercised
/// without a `tauri::AppHandle` - `Host` needs one to build at all, which is not available
/// in a unit test.
///
/// `AlreadyPresent` and `WouldOverwrite` both bind and return `Ok`. There is no other path
/// in this UI to bind a capture onto a track that already exists, so refusing here would
/// strand anyone who adds the same track name twice (a
/// crash, a later Unbind, a hand-edited bindings.json) with a track they can never use. In
/// both cases `tracks::import` returns without inserting, so the existing row - and
/// whatever gates it holds - is never touched; only bindings.json, a file this app owns,
/// changes.
fn create_track_job(
    resource_dir: &Path,
    db: &Path,
    root: &Path,
    display: &str,
    capture: &str,
    log: &mut dyn FnMut(String),
) -> Result<(), String> {
    let seed_path = resource_dir.join("seed.track.json");
    let seed = std::fs::read_to_string(&seed_path)
        .map_err(|e| format!("seed template missing at {}: {e}", seed_path.display()))?;
    let (scene, kind, value) = tracks::seed_value(&seed).map_err(|e| e.to_string())?;

    if !db.is_file() {
        return Err(format!(
            "VelociDrone's database is not there yet - run the game once. ({})",
            db.display()
        ));
    }
    let stored = tracks::stored_name(display);
    match tracks::import(db, &stored, scene, kind, &value).map_err(|e| e.to_string())? {
        (tracks::ImportResult::Added, _) => log(format!("added track \"{display}\"")),
        (tracks::ImportResult::AlreadyPresent, _) => {
            log(format!("track \"{display}\" is already there, unchanged"))
        }
        (tracks::ImportResult::WouldOverwrite, _) => log(format!(
            "a track called \"{display}\" already exists - using it as-is"
        )),
    }

    game::bind(root, display, capture, false).map_err(|e| e.to_string())?;
    log(format!("bound \"{display}\" to {capture}"));
    Ok(())
}

/// Whether a capture's name matches a catalog entry's `install_as` - the same match
/// `resolveCatalogId` in `web/src/pages/Tracks.tsx` uses to decide whether a track's
/// missing capture can offer a Get button. Reused here for the mirror question: if a
/// capture is about to be deleted, can it come back? No catalog loaded (fetch failed, or
/// never ran) reads the same as "no match" - the honest answer is "can't tell", and the
/// safe default is the same warning given for a capture that truly cannot be recovered.
fn capture_is_fetchable(name: &str, catalog: Option<&[catalog::Entry]>) -> bool {
    catalog
        .into_iter()
        .flatten()
        .any(|e| e.install_as.as_deref() == Some(name))
}

/// True if some track other than `track` also has `capture` in its bound list.
///
/// A capture is not owned by the track that names it - the same `.ply` can sit under
/// more than one track's key (`game::bind` overwrites a track's own entry with a fresh
/// single-element list, but nothing stops a second track from being bound to the same
/// name). This is the check that keeps a shared capture alive when only one of its
/// tracks is removed.
fn capture_used_elsewhere(bindings: &game::Bindings, track: &str, capture: &str) -> bool {
    bindings
        .iter()
        .any(|(k, v)| k != track && v.iter().any(|c| c == capture))
}

/// Joins quoted names as "a", "a and b", or "a, b and c".
fn join_with_and(quoted: &[String]) -> String {
    match quoted.split_last() {
        Some((last, rest)) if !rest.is_empty() => format!("{} and {last}", rest.join(", ")),
        _ => quoted.join(""),
    }
}

/// The clause naming captures that are actually going, and whether each can be fetched
/// again. Only ever called with a non-empty list - callers decide separately what to say
/// when nothing is being deleted.
fn deletion_clause(captures: &[String], catalog: Option<&[catalog::Entry]>) -> String {
    let quoted: Vec<String> = captures.iter().map(|c| format!("\"{c}\"")).collect();
    let names = join_with_and(&quoted);
    let (noun, verb) = if captures.len() == 1 {
        ("capture", "goes")
    } else {
        ("captures", "go")
    };
    let fetchable: Vec<bool> = captures
        .iter()
        .map(|c| capture_is_fetchable(c, catalog))
        .collect();

    if fetchable.iter().all(|f| *f) {
        format!("Its {noun} {names} {verb} too, and can be fetched again from the catalog.")
    } else if fetchable.iter().all(|f| !*f) {
        let pronoun = if captures.len() == 1 { "it" } else { "they" };
        format!(
            "Its {noun} {names} {verb} too, and without the original .ply {pronoun} \
             cannot be recovered."
        )
    } else {
        // A mix only happens with two or more bound captures, one fetchable and one not -
        // rare enough that naming each one's own fate plainly beats a single blended
        // sentence trying to cover both cases at once.
        let parts: Vec<String> = captures
            .iter()
            .zip(fetchable.iter())
            .map(|(c, f)| {
                if *f {
                    format!("\"{c}\" can be fetched again from the catalog")
                } else {
                    format!("\"{c}\" cannot be recovered without the original .ply")
                }
            })
            .collect();
        format!("Its captures go too: {}.", parts.join(", "))
    }
}

/// The sentence [`Host::remove_track`]'s confirmation dialog folds into each of its three
/// branches. `to_delete` and `kept_shared` are the same track's bound captures, already
/// split by whether some other track's binding still names them (see
/// [`capture_used_elsewhere`]) - a capture another track still uses is never going to be
/// deleted, so the dialog must never say it is. `None` when the track has no bound capture
/// at all, so a track that was never bound to anything keeps the plain "only the binding
/// goes" wording it always had.
fn capture_removal_sentence(
    to_delete: &[String],
    kept_shared: &[String],
    catalog: Option<&[catalog::Entry]>,
) -> Option<String> {
    if to_delete.is_empty() && kept_shared.is_empty() {
        return None;
    }
    if to_delete.is_empty() {
        // Every capture this track had is still named by some other track's binding -
        // removing this track deletes no capture at all, and the dialog must say that
        // plainly rather than naming a deletion that will not happen.
        let quoted: Vec<String> = kept_shared.iter().map(|c| format!("\"{c}\"")).collect();
        let names = join_with_and(&quoted);
        return Some(if kept_shared.len() == 1 {
            format!("None of its captures are going - {names} is still used by another track.")
        } else {
            format!("None of its captures are going - {names} are still used by other tracks.")
        });
    }
    let mut sentence = deletion_clause(to_delete, catalog);
    if !kept_shared.is_empty() {
        let quoted: Vec<String> = kept_shared.iter().map(|c| format!("\"{c}\"")).collect();
        let names = join_with_and(&quoted);
        sentence.push(' ');
        sentence.push_str(&if kept_shared.len() == 1 {
            format!("Its capture {names} stays where it is - another track still uses it.")
        } else {
            format!("Its captures {names} stay where they are - other tracks still use them.")
        });
    }
    Some(sentence)
}

/// The logic behind [`Host::remove_track`], pulled out of the method so it can be
/// exercised without a `tauri::AppHandle`, same as [`create_track_job`] above.
///
/// Order matters for safety: unbind first (`bindings.json` is ours, never the game's, so
/// this step alone needs no `is_running` guard), then the database row (only for a track
/// this person owns - a server track's row is never touched), then the capture files
/// last. If capture removal fails partway, the track and its binding are already gone
/// rather than leaving a track that still looks like it points at a capture that might no
/// longer be fully there.
///
/// `captures` is the full list this track was bound to, unfiltered - not the dialog's
/// `to_delete`/`kept_shared` split. That split was computed before the confirmation the
/// person may have sat on for a while, so it can be stale by the time this runs (another
/// track could have been bound to the same capture in the meantime, or unbound from it).
/// This re-reads `bindings.json` itself, right after `unbind` and right before any file
/// would be deleted, and skips whatever some other track's entry still names - the same
/// [`capture_used_elsewhere`] question, asked again against current state instead of a
/// snapshot.
fn remove_track_job(
    root: &Path,
    db: &Path,
    name: &str,
    mine: bool,
    captures: &[String],
    log: &mut dyn FnMut(String),
) -> Result<(), String> {
    // Before anything is written: the game holds the database and the capture files, and
    // a refusal after the unbind would leave the track half-removed.
    if (mine || !captures.is_empty()) && launch::is_running() {
        return Err(
            "VelociDrone is running. Close it first - it keeps its track database and captures open."
                .into(),
        );
    }
    if game::unbind(root, name).map_err(|e| e.to_string())? {
        log(format!("unbound \"{name}\""));
    }

    if mine {
        let (removed, backup) = tracks::remove(db, name).map_err(|e| e.to_string())?;
        if removed {
            let backup_name = backup
                .as_ref()
                .and_then(|p| p.file_name())
                .and_then(|s| s.to_str())
                .unwrap_or("?");
            log(format!(
                "removed track \"{name}\" (backup: {backup_name})"
            ));
        } else {
            log("the track was already gone from the database".into());
        }
    }

    if captures.is_empty() {
        return Ok(());
    }

    // `name`'s own entry is already gone (the unbind above), so anything still turning up
    // here belongs to some other track. A read that fails (corrupt file, mid-write by
    // something else) is not treated as "nothing else uses it" - the safe default when we
    // cannot tell is to leave every capture in place, not to delete on a guess.
    let still_bound = match game::try_read_bindings(root) {
        Ok(b) => Some(b),
        Err(e) => {
            log(format!(
                "could not confirm which captures are still shared ({e}) - leaving them all in place"
            ));
            None
        }
    };
    for capture in captures {
        let shared = match &still_bound {
            Some(b) => b.values().any(|v| v.iter().any(|c| c == capture)),
            None => true,
        };
        if shared {
            log(format!(
                "kept capture \"{capture}\" - another track still uses it"
            ));
            continue;
        }
        if game::remove_capture(root, capture).map_err(|e| e.to_string())? {
            log(format!("removed capture \"{capture}\""));
        } else {
            log(format!("capture \"{capture}\" was not there"));
        }
    }

    Ok(())
}

struct Snapshot {
    game: Option<PathBuf>,
    catalog: Option<Vec<catalog::Entry>>,
    catalog_error: Option<String>,
    app_update: Option<String>,
    catalog_url: String,
    busy: Option<String>,
    busy_percent: Option<u8>,
}

fn resolve_picked_game(path: PathBuf) -> Option<PathBuf> {
    if game::is_game(&path) {
        return Some(path);
    }
    // macOS: the picker may land on the parent of the .app bundle.
    #[cfg(target_os = "macos")]
    {
        let nested = path.join("velocidrone.app");
        if game::is_game(&nested) {
            return Some(nested);
        }
    }
    None
}

/// Prefer `resource_dir/mod`, then `resource_dir/resources/mod` (bundled .app layout).
fn resolve_resource_dir(app: &tauri::AppHandle) -> PathBuf {
    let base = app
        .path()
        .resource_dir()
        .unwrap_or_else(|_| PathBuf::from("."));
    if game::bundled_mod_dir(&base).is_some() || base.join("mod").is_dir() {
        return base;
    }
    let nested = base.join("resources");
    if game::bundled_mod_dir(&nested).is_some() || nested.join("mod").is_dir() {
        return nested;
    }
    base
}

/// The clock lives in `tracks`, which needs the same wall-clock answer for the `date`
/// column and for backup filenames. Two copies of the same `localtime_r` block were here
/// and there before the Windows port, and adding a second `GetLocalTime` beside them would
/// have made it three.
fn now_hms() -> String {
    let (_y, _mo, _d, h, mi, s) = tracks::local_ymdhms();
    format!("{h:02}:{mi:02}:{s:02}")
}

/// Every command the page can send.
///
/// `async` is load-bearing rather than decorative. A plain `#[tauri::command]` runs on the
/// main thread, and half of these open a modal - pick a folder, confirm a removal, warn
/// about a name clash. The dialog plugin posts the dialog to the main thread and then
/// blocks waiting for the answer, so from the main thread it waits for a window it is
/// itself preventing from ever being drawn. The app freezes with a picker on screen that
/// does not respond to Escape, to its own Cancel button, or to anything else.
#[tauri::command(async)]
fn dispatch(
    host: tauri::State<'_, Arc<Host>>,
    cmd: String,
    id: Option<String>,
    arg: Option<serde_json::Value>,
) {
    let h = Arc::clone(&host);
    // addTrack reads `path` and `name` together out of `arg` - one id string was
    // never going to carry two values (bridge.ts's send() widened for exactly this).
    let field = |k: &str| -> Option<String> {
        arg.as_ref()
            .and_then(|v| v.get(k))
            .and_then(|v| v.as_str())
            .map(|s| s.to_string())
    };
    match cmd.as_str() {
        "refresh" => h.push(),
        "pick" => h.pick_game(),
        "installMod" => h.install_mod(),
        "uninstallMod" => h.uninstall_mod(),
        "pickPly" => h.pick_ply(),
        "refreshCatalog" => h.refresh_catalog(),
        "openAppUpdate" => h.open_app_update(),
        "get" => {
            if let Some(id) = id {
                h.get_from_catalog(&id, false);
            }
        }
        "replace" => {
            if let Some(id) = id {
                h.get_from_catalog(&id, true);
            }
        }
        "removeTrack" => {
            if let Some(id) = id {
                h.remove_track(&id);
            }
        }
        "unbindTrack" => {
            if let Some(id) = id {
                h.unbind_track(&id);
            }
        }
        "removeCapture" => {
            if let Some(id) = id {
                h.remove_capture(&id);
            }
        }
        "addTrack" => {
            if let (Some(path), Some(name)) = (field("path"), field("name")) {
                h.add_track(&path, &name);
            }
        }
        "fly" => h.launch(),
        _ => {}
    }
}

pub fn run() {
    tauri::Builder::default()
        // Registered before anything else, because a second launch has to be turned away
        // before it starts building state of its own.
        //
        // Two windows are two answers to the same questions. Both scan for the game, both
        // offer to install over each other, and installing from one while the other is
        // mid-download writes the same folder twice. The C# companion held a named mutex
        // for exactly this and handed the existing window back instead of refusing, which
        // is the right shape: the second launch is nearly always someone who lost the
        // first window behind the game.
        //
        // The command line is safe from this. `main` answers --export-track and
        // --check-catalog and exits before the builder runs, so a second invocation that
        // carries one of them never reaches the plugin and is never redirected into the
        // running window.
        .plugin(tauri_plugin_single_instance::init(|app, _argv, _cwd| {
            let Some(w) = app.get_webview_window("main") else {
                return;
            };
            // Unminimise first. Asking for focus on a minimised window flashes the taskbar
            // and leaves it minimised, which reads as the click having done nothing - the
            // same order the C# used, for the same reason.
            let _ = w.unminimize();
            let _ = w.show();
            let _ = w.set_focus();
        }))
        // Position and size only - not maximized, not visibility. This window is something
        // you alt-tab to from a fullscreen game; restoring it maximized because it happened
        // to be maximized once is worse than restoring the modest size it usually sits at.
        // Leaving MAXIMIZED out of the flags means the plugin never calls `maximize()` on
        // restore. VISIBLE is left out for a narrower reason - with it
        // unset the plugin never calls `show()`/`set_focus()` after restoring, and the
        // window is created visible by `tauri.conf.json` regardless, so there is nothing to
        // gain from tracking it and a hidden-on-quit window has no way back without editing
        // the state file by hand.
        .plugin(
            tauri_plugin_window_state::Builder::new()
                .with_state_flags(
                    tauri_plugin_window_state::StateFlags::POSITION
                        | tauri_plugin_window_state::StateFlags::SIZE,
                )
                .build(),
        )
        .plugin(tauri_plugin_dialog::init())
        // api.ts's hosted transport goes through this rather than a same-origin fetch from
        // the webview - the plugin's HTTP server has no Access-Control-Allow-Origin (adding
        // one would open its LAN-facing API to any site), so a fetch from the webview's
        // origin would just fail. Routing through the host keeps that header absent.
        .plugin(tauri_plugin_http::init())
        .setup(|app| {
            let resource_dir = resolve_resource_dir(app.handle());
            let settings = Settings::load();
            // Only the remembered path is resolved here, and only because it costs one
            // stat. Guessing is not: on Windows it probes every drive letter, and an empty
            // removable drive or a mapped share that is no longer there answers on its own
            // schedule. Everything on this line runs before the state is reachable, so a
            // slow answer here is a window that never fills in.
            let game = settings
                .game
                .as_ref()
                .map(PathBuf::from)
                .filter(|p| game::is_game(p));
            let host = Arc::new(Host {
                app: app.handle().clone(),
                resource_dir,
                inner: Mutex::new(Inner {
                    settings,
                    game,
                    catalog: None,
                    catalog_error: None,
                    app_update: None,
                    busy: None,
                    busy_percent: None,
                    running: launch::is_running(),
                    child: None,
                }),
            });
            // Managed before anything else is started. The page asks for state exactly
            // once, on subscribe, and `bridge.ts` voids the rejection — so a `refresh` that
            // lands before the state is managed fails, is dropped, and is never retried:
            // an empty window with nothing to say why. The window is created before this
            // hook runs, so the race is real, and everything above this line is inside it.
            app.manage(Arc::clone(&host));

            let locate = Arc::clone(&host);
            std::thread::spawn(move || locate.locate_game());

            let watch = Arc::clone(&host);
            std::thread::spawn(move || loop {
                std::thread::sleep(Duration::from_secs(2));
                {
                    let mut i = watch.inner.lock().unwrap();
                    if let Some(child) = i.child.as_mut() {
                        match child.try_wait() {
                            Ok(Some(_)) => i.child = None,
                            Ok(None) => {}
                            Err(_) => i.child = None,
                        }
                    }
                }
                let now = launch::is_running();
                {
                    let mut i = watch.inner.lock().unwrap();
                    if i.running == now {
                        continue;
                    }
                    i.running = now;
                }
                watch.post(json!({"type":"running","running": now}));
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![dispatch])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    const SEED: &str = r#"{"name":"VDGS Template","scene_id":16,"type":0,"value":"{\"gates\":[{\"start\":true}]}"}"#;

    fn tmp() -> PathBuf {
        static N: AtomicU32 = AtomicU32::new(0);
        let p = std::env::temp_dir().join(format!(
            "vdgs-lib-{}-{}",
            std::process::id(),
            N.fetch_add(1, Ordering::SeqCst)
        ));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p).unwrap();
        p
    }

    fn resource_dir_with_seed() -> PathBuf {
        let dir = tmp();
        std::fs::write(dir.join("seed.track.json"), SEED).unwrap();
        dir
    }

    /// Same schema as `tracks::tests::fresh_db` - kept as its own copy because that one is
    /// private to the `tracks` module.
    fn fresh_db() -> PathBuf {
        let p = tmp().join("user11.db");
        let c = rusqlite::Connection::open(&p).unwrap();
        c.execute_batch("CREATE TABLE [tracks] ([id] INTEGER NOT NULL PRIMARY KEY, [scene_id] INTEGER NOT NULL, [name] VARCHAR, [value] VARCHAR, [protected_track] TINYINT(1) NOT NULL DEFAULT 0, online_id int default 0, rating int default 0, favourite int default 0, date varchar default '2019-07-01 00:00:00', type int default 0);").unwrap();
        p
    }

    // Add track is one job: the copy and the track must both exist afterwards, bound by
    // the capture's on-disk name (the file stem), not by anything the person typed.
    #[test]
    fn add_track_job_installs_the_ply_and_binds_a_new_track_to_it() {
        let resource_dir = resource_dir_with_seed();
        let db = fresh_db();
        let root = tmp();
        let src = tmp().join("himeji-lod2.ply");
        std::fs::write(&src, b"ply\nelement vertex 3\nend_header\n").unwrap();

        let mut logged = Vec::new();
        let result = add_track_job(&resource_dir, &db, &root, &src, "VDGS Himeji", &mut |s| {
            logged.push(s)
        });

        assert!(result.is_ok(), "{result:?}");
        assert!(root.join("vdgs/himeji-lod2.ply").is_file(), "the capture must be copied in");
        assert!(tracks::find(&db, "VDGS Himeji").unwrap().is_some(), "the track row must exist");
        assert_eq!(
            game::read_bindings(&root)["VDGS Himeji"],
            vec!["himeji-lod2".to_string()],
            "the binding must name the file stem"
        );
        assert!(logged.iter().any(|l| l.contains("installed himeji-lod2")));
        assert!(logged.iter().any(|l| l.contains("added track")));
    }

    // A source that cannot be read stops before the database is touched: no half-made
    // track pointing at a capture that never arrived.
    #[test]
    fn add_track_job_writes_no_track_when_the_ply_cannot_be_copied() {
        let resource_dir = resource_dir_with_seed();
        let db = fresh_db();
        let root = tmp();
        let missing = tmp().join("nope.ply");

        let mut logged = Vec::new();
        let result = add_track_job(&resource_dir, &db, &root, &missing, "VDGS Nope", &mut |s| {
            logged.push(s)
        });

        assert!(result.is_err());
        assert!(tracks::find(&db, "VDGS Nope").unwrap().is_none());
        assert!(!root.join("vdgs/bindings.json").exists());
    }

    #[test]
    fn create_track_job_binds_an_already_present_track_without_touching_its_value() {
        let resource_dir = resource_dir_with_seed();
        let db = fresh_db();
        let root = tmp();
        let (_, _, seed_value) = tracks::seed_value(SEED).unwrap();

        // The seed already cloned under this name - identical value, so `import` reports
        // AlreadyPresent rather than inserting a second row.
        let stored = tracks::stored_name("VDGS my house");
        tracks::import(&db, &stored, 16, 0, &seed_value).unwrap();

        let mut logged = Vec::new();
        let result = create_track_job(
            &resource_dir,
            &db,
            &root,
            "VDGS my house",
            "my-house",
            &mut |s| logged.push(s),
        );

        assert!(result.is_ok(), "{result:?}");
        let row = tracks::find(&db, "VDGS my house").unwrap().unwrap();
        assert_eq!(row.value, seed_value, "the existing row's gates must be untouched");
        assert_eq!(
            game::read_bindings(&root)["VDGS my house"],
            vec!["my-house".to_string()],
            "the capture must still be bound"
        );
        assert!(logged.iter().any(|l| l.contains("already there")));
    }

    #[test]
    fn create_track_job_binds_a_would_overwrite_track_without_touching_its_value() {
        let resource_dir = resource_dir_with_seed();
        let db = fresh_db();
        let root = tmp();

        // A track the person built themselves - same name, different gates. `import`
        // reports WouldOverwrite and, per the guard in `tracks::import`, never writes.
        let own_value = "{\"gates\":[{\"start\":true},{\"finish\":true},{\"finish\":true}]}";
        let stored = tracks::stored_name("VDGS my house");
        tracks::import(&db, &stored, 16, 0, own_value).unwrap();

        let mut logged = Vec::new();
        let result = create_track_job(
            &resource_dir,
            &db,
            &root,
            "VDGS my house",
            "my-house",
            &mut |s| logged.push(s),
        );

        assert!(result.is_ok(), "{result:?}");
        let row = tracks::find(&db, "VDGS my house").unwrap().unwrap();
        assert_eq!(
            row.value, own_value,
            "a track the person built themselves must never be rewritten with the seed's gates"
        );
        assert_eq!(
            game::read_bindings(&root)["VDGS my house"],
            vec!["my-house".to_string()],
            "the capture must still be bound onto the existing track"
        );
        assert!(logged.iter().any(|l| l.contains("already exists")));
    }

    #[test]
    fn create_track_job_finds_an_editor_written_row_spelled_with_a_literal_space() {
        let resource_dir = resource_dir_with_seed();
        let db = fresh_db();
        let root = tmp();

        // VelociDrone's own track editor writes a literal space, not the '+'-encoded
        // spelling `stored_name` produces for a companion-created track. Both spellings
        // are valid rows in `user11.db` side by side (AGENTS.md's "a course has two
        // spellings") - this row stands in for one built by hand in the editor, with
        // gates `create_track_job` must never touch.
        let editor_value = "{\"gates\":[{\"start\":true},{\"finish\":true},{\"finish\":true}]}";
        {
            let c = rusqlite::Connection::open(&db).unwrap();
            c.execute(
                "insert into tracks (scene_id, name, value, type) values (16, 'VDGS my house', ?1, 0)",
                [editor_value],
            )
            .unwrap();
        }

        let mut logged = Vec::new();
        let result = create_track_job(
            &resource_dir,
            &db,
            &root,
            "VDGS my house",
            "my-house",
            &mut |s| logged.push(s),
        );

        assert!(result.is_ok(), "{result:?}");
        let rows = tracks::list(&db).unwrap();
        assert_eq!(
            rows.len(),
            1,
            "the encoded spelling must resolve to the editor's row, not add a second one"
        );
        assert_eq!(
            rows[0].value, editor_value,
            "the editor-built track's gates must be byte-for-byte untouched"
        );
        assert_eq!(
            game::read_bindings(&root)["VDGS my house"],
            vec!["my-house".to_string()],
            "the capture must bind onto the existing editor-written track"
        );
    }

    #[test]
    fn create_track_job_reports_a_missing_database_instead_of_a_raw_sqlite_error() {
        let resource_dir = resource_dir_with_seed();
        let db = tmp().join("does-not-exist.db");
        let root = tmp();

        let mut logged = Vec::new();
        let err = create_track_job(
            &resource_dir,
            &db,
            &root,
            "VDGS my house",
            "my-house",
            &mut |s| logged.push(s),
        )
        .unwrap_err();

        assert!(
            err.contains("run the game once"),
            "expected the friendly not-yet-run message, got: {err}"
        );
        assert!(!err.to_ascii_lowercase().contains("sqlite"));
    }

    #[test]
    fn remove_track_job_deletes_its_bound_capture_directory() {
        let db = fresh_db();
        let root = tmp();
        let stored = tracks::stored_name("VDGS my house");
        tracks::import(&db, &stored, 16, 0, "{}").unwrap();
        game::bind(&root, "VDGS my house", "my-house", false).unwrap();
        let capture_dir = root.join("vdgs/my-house");
        std::fs::create_dir_all(&capture_dir).unwrap();
        std::fs::write(capture_dir.join("meta.json"), b"{}").unwrap();

        let captures = vec!["my-house".to_string()];
        let mut logged = Vec::new();
        let result = remove_track_job(&root, &db, "VDGS my house", true, &captures, &mut |s| {
            logged.push(s)
        });

        assert!(result.is_ok(), "{result:?}");
        assert!(
            !capture_dir.exists(),
            "the capture must be deleted along with its track"
        );
        assert!(
            tracks::find(&db, "VDGS my house").unwrap().is_none(),
            "the track row must be gone"
        );
        assert!(
            !game::read_bindings(&root).contains_key("VDGS my house"),
            "the binding must be gone"
        );
        assert!(logged
            .iter()
            .any(|l| l.contains("removed capture \"my-house\"")));
    }

    #[test]
    fn remove_track_job_bound_to_two_captures_deletes_both() {
        let db = fresh_db();
        let root = tmp();
        let stored = tracks::stored_name("VDGS two scenes");
        tracks::import(&db, &stored, 16, 0, "{}").unwrap();
        let mut bindings = game::Bindings::new();
        bindings.insert(
            "VDGS two scenes".to_string(),
            vec!["scene-a".to_string(), "scene-b".to_string()],
        );
        game::write_bindings(&root, &bindings).unwrap();
        for name in ["scene-a", "scene-b"] {
            let dir = root.join("vdgs").join(name);
            std::fs::create_dir_all(&dir).unwrap();
            std::fs::write(dir.join("meta.json"), b"{}").unwrap();
        }

        let captures = vec!["scene-a".to_string(), "scene-b".to_string()];
        let mut logged = Vec::new();
        let result = remove_track_job(&root, &db, "VDGS two scenes", true, &captures, &mut |s| {
            logged.push(s)
        });

        assert!(result.is_ok(), "{result:?}");
        assert!(
            !root.join("vdgs/scene-a").exists(),
            "the first capture must be gone"
        );
        assert!(
            !root.join("vdgs/scene-b").exists(),
            "the second capture must be gone"
        );
        assert!(!game::read_bindings(&root).contains_key("VDGS two scenes"));
    }

    #[test]
    fn remove_track_job_leaves_an_unbound_capture_untouched() {
        // This only proves a capture the job's own `captures` list never named survives -
        // the loop could never have reached "unrelated-scene" regardless of sharing. The
        // sharing guard itself is covered by the two tests below.
        let db = fresh_db();
        let root = tmp();
        let stored = tracks::stored_name("VDGS my house");
        tracks::import(&db, &stored, 16, 0, "{}").unwrap();
        game::bind(&root, "VDGS my house", "my-house", false).unwrap();
        let unrelated = root.join("vdgs/unrelated-scene");
        std::fs::create_dir_all(&unrelated).unwrap();
        std::fs::write(unrelated.join("meta.json"), b"{}").unwrap();

        let captures = vec!["my-house".to_string()];
        let mut logged = Vec::new();
        let result = remove_track_job(&root, &db, "VDGS my house", true, &captures, &mut |s| {
            logged.push(s)
        });

        assert!(result.is_ok(), "{result:?}");
        assert!(
            unrelated.exists(),
            "a capture nothing points at must survive removing an unrelated track"
        );
    }

    #[test]
    fn remove_track_job_leaves_a_capture_two_tracks_share() {
        // Two tracks bound to the same capture, "shared". Removing track A must not
        // delete it - and must not disturb track B's own binding entry, a different key
        // in the same map, which still has to resolve to that surviving capture.
        let db = fresh_db();
        let root = tmp();
        let stored_a = tracks::stored_name("VDGS track A");
        tracks::import(&db, &stored_a, 16, 0, "{}").unwrap();
        let mut bindings = game::Bindings::new();
        bindings.insert("VDGS track A".to_string(), vec!["shared".to_string()]);
        bindings.insert("VDGS track B".to_string(), vec!["shared".to_string()]);
        game::write_bindings(&root, &bindings).unwrap();
        let capture_dir = root.join("vdgs/shared");
        std::fs::create_dir_all(&capture_dir).unwrap();
        std::fs::write(capture_dir.join("meta.json"), b"{}").unwrap();

        let captures = vec!["shared".to_string()];
        let mut logged = Vec::new();
        let result = remove_track_job(&root, &db, "VDGS track A", true, &captures, &mut |s| {
            logged.push(s)
        });

        assert!(result.is_ok(), "{result:?}");
        assert!(
            capture_dir.exists(),
            "a capture another track's binding still names must survive"
        );
        let remaining = game::read_bindings(&root);
        assert!(
            !remaining.contains_key("VDGS track A"),
            "the removed track's own binding entry must still be gone"
        );
        assert_eq!(
            remaining.get("VDGS track B"),
            Some(&vec!["shared".to_string()]),
            "the other track's binding must still resolve to the surviving capture"
        );
        assert!(logged.iter().any(|l| l.contains("kept capture \"shared\"")));
    }

    #[test]
    fn remove_track_job_deletes_the_unshared_capture_and_keeps_the_shared_one() {
        // "VDGS two scenes" is bound to two captures: "only-mine", which nothing else
        // uses, and "shared", which "VDGS other track" also uses. Removing "VDGS two
        // scenes" must take only the first.
        let db = fresh_db();
        let root = tmp();
        let stored = tracks::stored_name("VDGS two scenes");
        tracks::import(&db, &stored, 16, 0, "{}").unwrap();
        let mut bindings = game::Bindings::new();
        bindings.insert(
            "VDGS two scenes".to_string(),
            vec!["only-mine".to_string(), "shared".to_string()],
        );
        bindings.insert("VDGS other track".to_string(), vec!["shared".to_string()]);
        game::write_bindings(&root, &bindings).unwrap();
        for name in ["only-mine", "shared"] {
            let dir = root.join("vdgs").join(name);
            std::fs::create_dir_all(&dir).unwrap();
            std::fs::write(dir.join("meta.json"), b"{}").unwrap();
        }

        let captures = vec!["only-mine".to_string(), "shared".to_string()];
        let mut logged = Vec::new();
        let result = remove_track_job(&root, &db, "VDGS two scenes", true, &captures, &mut |s| {
            logged.push(s)
        });

        assert!(result.is_ok(), "{result:?}");
        assert!(
            !root.join("vdgs/only-mine").exists(),
            "the capture nothing else uses must be deleted"
        );
        assert!(
            root.join("vdgs/shared").exists(),
            "the capture another track still uses must survive"
        );
        let remaining = game::read_bindings(&root);
        assert_eq!(
            remaining.get("VDGS other track"),
            Some(&vec!["shared".to_string()]),
            "the other track's own binding must still resolve"
        );
    }

    #[test]
    fn remove_track_job_removes_the_row_and_the_binding_as_before() {
        // A track with no bound capture: unbind and the row removal still both happen,
        // unchanged from before this capture-deleting behaviour existed.
        let db = fresh_db();
        let root = tmp();
        let stored = tracks::stored_name("VDGS my house");
        tracks::import(&db, &stored, 16, 0, "{}").unwrap();
        game::bind(&root, "VDGS my house", "my-house", false).unwrap();

        let mut logged = Vec::new();
        let result = remove_track_job(&root, &db, "VDGS my house", true, &[], &mut |s| {
            logged.push(s)
        });

        assert!(result.is_ok(), "{result:?}");
        assert!(tracks::find(&db, "VDGS my house").unwrap().is_none());
        assert!(!game::read_bindings(&root).contains_key("VDGS my house"));
    }
}
