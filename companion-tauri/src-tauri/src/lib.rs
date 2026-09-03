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

    fn install_zip(self: &Arc<Self>) {
        let Some(app) = self.inner.lock().unwrap().game.clone() else {
            return;
        };
        let Some(picked) = self
            .app
            .dialog()
            .file()
            .add_filter("Zip archives", &["zip"])
            .blocking_pick_file()
        else {
            return;
        };
        let Ok(zip) = picked.into_path() else {
            return;
        };
        let label = zip
            .file_name()
            .and_then(|s| s.to_str())
            .unwrap_or("archive")
            .to_string();
        let what = format!("installing {label}");
        self.run_busy(&what, move |_host, log| {
            if launch::is_running() {
                return Err(
                    "VelociDrone is running. Close it first - files in use cannot be replaced."
                        .into(),
                );
            }
            let root = game::root(&app);
            game::install_archive(&root, &zip, &label, log).map_err(|e| e.to_string())
        });
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
                    log(format!("catalog: {} capture(s)", got.len()));
                    let mut i = host.inner.lock().unwrap();
                    i.catalog = Some(got);
                    i.catalog_error = None;
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

    fn get_from_catalog(self: &Arc<Self>, id: &str) {
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
                    parsed = Some(t);
                }

                let zip = catalog::download(&entry.scene, &temp, &mut |p| host.percent(Some(p)))
                    .map_err(|e| e.to_string())?;
                let install_result = (|| {
                    host.percent(None);
                    host.set_busy(Some(&format!("installing {}", entry.name)));
                    let label = entry
                        .install_as
                        .as_deref()
                        .unwrap_or(entry.name.as_str());
                    game::install_archive(&root, &zip, label, log).map_err(|e| e.to_string())
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
                    game::bind(&root, &shown, install_as).map_err(|e| e.to_string())?;
                    log(format!("bound \"{shown}\" to {install_as}"));
                }
                Ok(())
            })();

            if let Some(tf) = track_file {
                let _ = std::fs::remove_file(tf);
            }
            result
        });
    }

    fn remove_track(self: &Arc<Self>, name: &str) {
        let Some(app) = self.inner.lock().unwrap().game.clone() else {
            return;
        };
        let db = tracks::db_path();
        let row = if db.is_file() {
            tracks::find(&db, name).ok().flatten()
        } else {
            None
        };
        let mine = row.as_ref().is_some_and(|t| !t.from_server);
        let question = if mine {
            format!(
                "Remove the track \"{name}\" from VelociDrone?\n\n\
                 Its binding goes with it. The capture stays where it is, and the \
                 database is copied first."
            )
        } else if row.is_none() {
            format!(
                "Stop showing a capture on \"{name}\"?\n\n\
                 There is no such track in VelociDrone, so only the binding goes."
            )
        } else {
            format!(
                "Stop showing a capture on \"{name}\"?\n\n\
                 The track came from the official track server, so it is left alone - \
                 only the binding goes."
            )
        };
        if !self.confirm(&question) {
            return;
        }
        let name = name.to_string();
        self.run_busy(&format!("removing {name}"), move |_host, log| {
            let root = game::root(&app);
            if game::unbind(&root, &name).map_err(|e| e.to_string())? {
                log(format!("unbound \"{name}\""));
            }
            if !mine {
                return Ok(());
            }
            if launch::is_running() {
                return Err(
                    "VelociDrone is running. Close it first - it keeps its track database open."
                        .into(),
                );
            }
            let (removed, backup) = tracks::remove(&db, &name).map_err(|e| e.to_string())?;
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
            Ok(())
        });
    }

    fn add_track(&self) {
        let Some(app) = self.inner.lock().unwrap().game.clone() else {
            return;
        };
        let Some(picked) = self
            .app
            .dialog()
            .file()
            .add_filter("JSON", &["json"])
            .blocking_pick_file()
        else {
            return;
        };
        let Ok(path) = picked.into_path() else {
            return;
        };
        match self.add_track_inner(&app, &path) {
            Ok(()) => self.push(),
            Err(ex) => {
                self.log(&format!("failed: {ex}"));
                self.error_dialog(&ex);
            }
        }
    }

    fn add_track_inner(&self, app: &Path, path: &Path) -> Result<(), String> {
        if launch::is_running() {
            return Err("Close VelociDrone first - it keeps its track database open.".into());
        }
        let text = std::fs::read_to_string(path).map_err(|e| e.to_string())?;
        let t: tracks::TrackFile = serde_json::from_str(&text).map_err(|e| e.to_string())?;
        let db = tracks::db_path();
        if !db.is_file() {
            return Err(format!(
                "VelociDrone's database is not there yet - run the game once. ({})",
                db.display()
            ));
        }
        let value = t.value_string();
        let (result, backup) =
            tracks::import(&db, &t.name, t.scene_id, t.kind, &value).map_err(|e| e.to_string())?;
        match result {
            tracks::ImportResult::Added => {
                let backup_name = backup
                    .as_ref()
                    .and_then(|p| p.file_name())
                    .and_then(|s| s.to_str())
                    .unwrap_or("?");
                self.log(&format!(
                    "added track \"{}\" (backup: {backup_name})",
                    t.name
                ));
                self.bind_if_obvious(app, &t.name);
            }
            tracks::ImportResult::AlreadyPresent => {
                self.log(&format!(
                    "track \"{}\" is already there, unchanged",
                    t.name
                ));
                self.bind_if_obvious(app, &t.name);
            }
            tracks::ImportResult::WouldOverwrite => {
                self.log(&format!(
                    "a different track is already called \"{}\" - left alone",
                    t.name
                ));
                self.warn_dialog(&format!(
                    "You already have a track called \"{}\" and its layout \
                     differs from this one.\n\nIt has been left as it is. Rename or \
                     delete yours in the game if you want this version.",
                    t.name
                ));
            }
        }
        Ok(())
    }

    fn bind_if_obvious(&self, app: &Path, track_name: &str) {
        let root = game::root(app);
        let scenes = game::scenes(&root);
        if scenes.len() != 1 {
            self.log(&format!(
                "bind \"{track_name}\" to a capture at http://localhost:8777/ once flying"
            ));
            return;
        }
        let shown = tracks::display_name(track_name);
        let scene = &scenes[0].name;
        if let Err(e) = game::bind(&root, &shown, scene) {
            self.log(&format!("failed: {e}"));
            return;
        }
        self.log(&format!("bound \"{shown}\" to {scene}"));
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

struct Snapshot {
    game: Option<PathBuf>,
    catalog: Option<Vec<catalog::Entry>>,
    catalog_error: Option<String>,
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
fn dispatch(host: tauri::State<'_, Arc<Host>>, cmd: String, id: Option<String>) {
    let h = Arc::clone(&host);
    match cmd.as_str() {
        "refresh" => h.push(),
        "pick" => h.pick_game(),
        "installMod" => h.install_mod(),
        "uninstallMod" => h.uninstall_mod(),
        "installCapture" => h.install_zip(),
        "refreshCatalog" => h.refresh_catalog(),
        "get" => {
            if let Some(id) = id {
                h.get_from_catalog(&id);
            }
        }
        "removeTrack" => {
            if let Some(id) = id {
                h.remove_track(&id);
            }
        }
        "addTrack" => h.add_track(),
        "fly" => h.launch(),
        _ => {}
    }
}

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
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
