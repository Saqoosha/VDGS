//! Tauri host: dispatch, busy jobs, dialogs, and the running watcher.

pub mod bepinex;
pub mod catalog;
pub mod game;
pub mod launch;
pub mod settings;
pub mod state;
pub mod tracks;

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

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
    #[allow(dead_code)]
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
        if self.inner.lock().unwrap().busy.is_some() {
            return;
        }
        self.set_busy(Some(what));
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
        let Some(picked) = self.app.dialog().file().blocking_pick_folder() else {
            return;
        };
        let Ok(path) = picked.into_path() else {
            return;
        };
        let Some(app) = resolve_picked_game(path) else {
            self.warn_dialog("No velocidrone.app in that folder.");
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
            if !game::has_bepinex(&root) {
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
    let nested = path.join("velocidrone.app");
    if game::is_game(&nested) {
        return Some(nested);
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

fn now_hms() -> String {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0);
    let (_y, _mo, _d, h, mi, s) = local_hms(secs);
    format!("{h:02}:{mi:02}:{s:02}")
}

#[cfg(unix)]
fn local_hms(secs: i64) -> (i32, u32, u32, u32, u32, u32) {
    #[repr(C)]
    struct Tm {
        tm_sec: i32,
        tm_min: i32,
        tm_hour: i32,
        tm_mday: i32,
        tm_mon: i32,
        tm_year: i32,
        tm_wday: i32,
        tm_yday: i32,
        tm_isdst: i32,
        tm_gmtoff: i64,
        tm_zone: *const i8,
    }
    extern "C" {
        fn localtime_r(timep: *const i64, result: *mut Tm) -> *mut Tm;
    }
    unsafe {
        let mut tm = std::mem::MaybeUninit::<Tm>::zeroed();
        let ptr = localtime_r(&secs, tm.as_mut_ptr());
        if ptr.is_null() {
            return (1970, 1, 1, 0, 0, 0);
        }
        let tm = tm.assume_init();
        (
            tm.tm_year + 1900,
            (tm.tm_mon + 1) as u32,
            tm.tm_mday as u32,
            tm.tm_hour as u32,
            tm.tm_min as u32,
            tm.tm_sec as u32,
        )
    }
}

#[cfg(not(unix))]
fn local_hms(_secs: i64) -> (i32, u32, u32, u32, u32, u32) {
    (1970, 1, 1, 0, 0, 0)
}

#[tauri::command]
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
            let mut settings = Settings::load();
            let game = match settings
                .game
                .as_ref()
                .map(PathBuf::from)
                .filter(|p| game::is_game(p))
            {
                Some(g) => Some(g),
                None => {
                    let found = game::find();
                    if let Some(ref g) = found {
                        settings.game = Some(g.to_string_lossy().into_owned());
                        settings.save();
                    }
                    found
                }
            };
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
            let watch = Arc::clone(&host);
            std::thread::spawn(move || loop {
                std::thread::sleep(Duration::from_secs(2));
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
            app.manage(host);
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![dispatch])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
