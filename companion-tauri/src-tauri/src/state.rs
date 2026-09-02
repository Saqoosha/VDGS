//! Build the companion's setup state JSON.

use std::collections::{BTreeMap, HashSet};
use std::path::Path;
use std::time::Instant;

use serde::Serialize;

use crate::bepinex;
use crate::catalog;
use crate::game;
use crate::tracks;

#[derive(Serialize)]
pub struct TrackEntry {
    pub track: String,
    pub capture: Option<String>,
    pub splats: u64,
    pub bytes: u64,
    pub collision: bool,
    #[serde(rename = "captureInstalled")]
    pub capture_installed: bool,
    pub converted: bool,
    #[serde(rename = "inGame")]
    pub in_game: bool,
    #[serde(rename = "fromServer")]
    pub from_server: bool,
}

#[derive(Serialize)]
pub struct CatalogEntryOut {
    pub id: String,
    pub name: String,
    pub description: Option<String>,
    pub author: Option<String>,
    pub licence: Option<String>,
    pub splats: u64,
    pub bytes: u64,
    pub installed: bool,
}

#[derive(Serialize)]
pub struct CatalogOut {
    pub url: String,
    pub error: Option<String>,
    pub entries: Vec<CatalogEntryOut>,
}

#[derive(Serialize)]
pub struct Unbound {
    pub name: String,
    pub splats: u64,
    pub collision: bool,
    pub bytes: u64,
}

#[derive(Serialize)]
pub struct SetupState {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub game: Option<String>,
    pub r#mod: Option<String>,
    #[serde(rename = "bundledMod")]
    pub bundled_mod: Option<String>,
    pub missing: Vec<String>,
    pub ready: bool,
    pub running: bool,
    pub busy: Option<String>,
    #[serde(rename = "busyPercent")]
    pub busy_percent: Option<u8>,
    #[serde(rename = "stateMs")]
    pub state_ms: u64,
    #[serde(rename = "launchArgs")]
    pub launch_args: String,
    pub tracks: Vec<TrackEntry>,
    pub catalog: Option<CatalogOut>,
    pub unbound: Vec<Unbound>,
    /// VelociDrone's True Lens setting. None = unknown (no game, or the row is absent);
    /// only Some(true) must be shown as a warning — with it on the mod draws and nothing
    /// reaches the screen, and every log still says success.
    #[serde(rename = "trueLens")]
    pub true_lens: Option<bool>,
}

pub struct Inputs<'a> {
    pub game: Option<&'a Path>,
    pub resource_dir: &'a Path,
    pub catalog: Option<&'a [catalog::Entry]>,
    pub catalog_error: Option<&'a str>,
    pub catalog_url: &'a str,
    pub busy: Option<&'a str>,
    pub busy_percent: Option<u8>,
    pub running: bool,
}

pub fn build(i: Inputs) -> SetupState {
    let clock = Instant::now();

    let mut missing = Vec::new();
    let mut scenes = Vec::new();
    let mut tracks = Vec::new();
    let mut unbound = Vec::new();
    let mut mod_ver: Option<String> = None;
    let mut in_game: Option<BTreeMap<String, bool>> = None;
    let mut bound = game::Bindings::new();

    if let Some(app) = i.game {
        let root = game::root(app);
        // The patched loader specifically. One that is merely present may be the official
        // build, which cannot load anything on Apple Silicon - reporting that as satisfied
        // is how a machine ends up calling itself ready and drawing nothing.
        if !bepinex::is_ours(&root) {
            missing.push("BepInEx".into());
        }
        mod_ver = game::installed_mod_version(&root);
        if mod_ver.is_none() {
            missing.push("the mod".into());
        }
        if !root.join("vdgs/vdgs-shaders").is_file() {
            missing.push("the shader bundle".into());
        }

        scenes = game::scenes(&root);
        in_game = tracks_in_game();
        bound = game::read_bindings(&root);
        build_tracks(&scenes, in_game.as_ref(), &bound, &mut tracks, &mut unbound);
    }

    // Same db tracks_in_game() reads; a second pass over one small table is fine, and
    // keeps this independent of whether the tracks query succeeded.
    let true_lens = if i.game.is_some() {
        tracks::true_lens_on(&tracks::db_path())
    } else {
        None
    };

    let catalog = catalog_state(
        i.catalog,
        i.catalog_error,
        i.catalog_url,
        &scenes,
        in_game.as_ref(),
        &bound,
    );

    SetupState {
        kind: "state",
        game: i.game.map(|p| p.to_string_lossy().into_owned()),
        r#mod: mod_ver,
        bundled_mod: game::bundled_mod_version(i.resource_dir),
        ready: i.game.is_some() && missing.is_empty(),
        missing,
        running: i.running,
        busy: i.busy.map(|s| s.to_string()),
        busy_percent: i.busy_percent,
        state_ms: clock.elapsed().as_millis() as u64,
        launch_args: String::new(),
        tracks,
        catalog,
        unbound,
        true_lens,
    }
}

/// Catalog.cs TrackInPlace — capture alone is enough when nothing is published;
/// otherwise the displayed track name must be in the DB and bound to at least one capture.
pub fn track_in_place(
    e: &catalog::Entry,
    in_game: Option<&BTreeMap<String, bool>>,
    bound: &game::Bindings,
) -> bool {
    if e.track.is_none() {
        return true;
    }
    let Some(track_name) = e.track_name.as_deref() else {
        return false;
    };
    let shown = tracks::display_name(track_name);
    let Some(map) = in_game else {
        return false;
    };
    if !map.contains_key(&shown) {
        return false;
    }
    match bound.get(&shown) {
        Some(captures) if !captures.is_empty() => true,
        _ => false,
    }
}

/// Keyed by display_name; None if db missing/unreadable.
pub fn tracks_in_game() -> Option<BTreeMap<String, bool>> {
    let db = tracks::db_path();
    if !db.is_file() {
        return None;
    }
    let list = tracks::list(&db).ok()?;
    let mut map = BTreeMap::new();
    for t in list {
        map.insert(tracks::display_name(&t.name), t.from_server);
    }
    Some(map)
}

fn catalog_state(
    catalog: Option<&[catalog::Entry]>,
    catalog_error: Option<&str>,
    catalog_url: &str,
    scenes: &[game::SceneInfo],
    in_game: Option<&BTreeMap<String, bool>>,
    bound: &game::Bindings,
) -> Option<CatalogOut> {
    if catalog.is_none() && catalog_error.is_none() {
        return None;
    }

    let entries = catalog
        .unwrap_or(&[])
        .iter()
        .map(|e| {
            let have_capture = e.install_as.as_ref().is_some_and(|as_name| {
                scenes
                    .iter()
                    .any(|s| s.name.eq_ignore_ascii_case(as_name))
            });
            CatalogEntryOut {
                id: e.id.clone(),
                name: e.name.clone(),
                description: e.description.clone(),
                author: e.author.clone(),
                licence: e.licence.clone(),
                splats: e.splats,
                bytes: e.bytes(),
                installed: have_capture && track_in_place(e, in_game, bound),
            }
        })
        .collect();

    Some(CatalogOut {
        url: catalog_url.to_string(),
        error: catalog_error.map(|s| s.to_string()),
        entries,
    })
}

fn build_tracks(
    scenes: &[game::SceneInfo],
    in_game: Option<&BTreeMap<String, bool>>,
    bound: &game::Bindings,
    tracks: &mut Vec<TrackEntry>,
    unbound: &mut Vec<Unbound>,
) {
    let mut named = HashSet::new();
    for (key, names) in bound {
        let mut splats = 0u64;
        let mut bytes = 0u64;
        let mut collision = !names.is_empty();
        let mut installed = !names.is_empty();
        let mut converted = true;

        for name in names {
            named.insert(name.to_ascii_lowercase());
            let info = scenes.iter().find(|s| s.name.eq_ignore_ascii_case(name));
            let Some(info) = info else {
                installed = false;
                continue;
            };
            splats += info.splats;
            bytes += info.bytes;
            collision &= info.collision;
            converted &= info.converted;
        }

        tracks.push(TrackEntry {
            track: key.clone(),
            capture: if names.is_empty() {
                None
            } else {
                Some(names.join(" + "))
            },
            splats,
            bytes,
            collision,
            capture_installed: installed,
            converted,
            in_game: in_game.is_none() || in_game.is_some_and(|m| m.contains_key(key)),
            from_server: in_game.is_some_and(|m| m.get(key).copied().unwrap_or(false)),
        });
    }

    for s in scenes {
        if !named.contains(&s.name.to_ascii_lowercase()) {
            unbound.push(Unbound {
                name: s.name.clone(),
                splats: s.splats,
                collision: s.collision,
                bytes: s.bytes,
            });
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};
    use std::path::PathBuf;

    #[test]
    fn track_in_place_rules() {
        let mut e = entry("VDGS+X", Some("x-dir"));
        let mut in_game = BTreeMap::new();
        in_game.insert("VDGS X".to_string(), false);
        let mut bound = game::Bindings::new();
        assert!(!track_in_place(&e, Some(&in_game), &bound));
        bound.insert("VDGS X".into(), vec!["x-dir".into()]);
        assert!(track_in_place(&e, Some(&in_game), &bound));
        assert!(!track_in_place(&e, None, &bound)); // unreadable db = not done
        e.track = None;
        assert!(track_in_place(&e, None, &bound)); // nothing published = done
        e.track = Some(catalog::FileRef {
            url: "u".into(),
            bytes: 0,
            sha256: None,
        });
        e.track_name = None;
        assert!(!track_in_place(&e, Some(&in_game), &bound));
    }

    #[test]
    fn build_marks_missing_and_bindings() {
        let root = tmp();
        let app = root.join("velocidrone.app");
        std::fs::create_dir_all(app.join("Contents/MacOS")).unwrap();
        std::fs::write(app.join("Contents/MacOS/velocidrone"), b"").unwrap();
        std::fs::create_dir_all(root.join("vdgs/a")).unwrap();
        std::fs::write(root.join("vdgs/a/meta.json"), r#"{"splatCount":3}"#).unwrap();
        game::bind(&root, "T", "a").unwrap();
        game::bind(&root, "U", "zzz").unwrap();
        let s = build(Inputs {
            game: Some(&app),
            resource_dir: &root,
            catalog: None,
            catalog_error: None,
            catalog_url: catalog::DEFAULT_URL,
            busy: None,
            busy_percent: None,
            running: false,
        });
        assert_eq!(s.missing, vec!["BepInEx", "the mod", "the shader bundle"]);
        assert!(!s.ready);
        assert_eq!(s.launch_args, "");
        let t: Vec<_> = s
            .tracks
            .iter()
            .map(|t| (t.track.as_str(), t.capture_installed))
            .collect();
        assert_eq!(t, vec![("T", true), ("U", false)]);
        assert!(s.unbound.is_empty());
        assert!(s.catalog.is_none());
    }

    fn entry(track_name: &str, install_as: Option<&str>) -> catalog::Entry {
        catalog::Entry {
            id: "id".into(),
            name: "n".into(),
            description: None,
            author: None,
            licence: None,
            splats: 0,
            scene: catalog::FileRef {
                url: "u".into(),
                bytes: 0,
                sha256: None,
            },
            install_as: install_as.map(|s| s.to_string()),
            track: Some(catalog::FileRef {
                url: "u".into(),
                bytes: 0,
                sha256: None,
            }),
            track_name: Some(track_name.to_string()),
        }
    }

    fn tmp() -> PathBuf {
        static N: AtomicU32 = AtomicU32::new(0);
        let p = std::env::temp_dir().join(format!(
            "vdgs-state-{}-{}",
            std::process::id(),
            N.fetch_add(1, Ordering::SeqCst)
        ));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p).unwrap();
        p
    }
}
