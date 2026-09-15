//! Find VelociDrone and manage mod files beside it.

use std::collections::{BTreeMap, HashSet, VecDeque};
use std::fs::{self, File};
use std::io::{self, BufRead, BufReader};
use std::path::{Path, PathBuf};
#[cfg(target_os = "macos")]
use std::time::SystemTime;

use serde::Serialize;

use crate::catalog;

/// macOS: PatchKit's `Apps/<hash>/Data/velocidrone.app`, newest by mtime.
#[cfg(target_os = "macos")]
pub fn find() -> Option<PathBuf> {
    let home = dirs::home_dir()?;
    let apps = home.join("Library/Application Support/PatchKit/Apps");
    let entries = fs::read_dir(&apps).ok()?;
    let mut best: Option<(SystemTime, PathBuf)> = None;
    for entry in entries.flatten() {
        let app = entry.path().join("Data").join("velocidrone.app");
        if !is_game(&app) {
            continue;
        }
        let modified = match fs::metadata(&app).and_then(|m| m.modified()) {
            Ok(t) => t,
            Err(_) => continue,
        };
        match &best {
            Some((t, _)) if modified <= *t => {}
            _ => best = Some((modified, app)),
        }
    }
    best.map(|(_, p)| p)
}

/// Windows: the named + drive guesses only (GameInstall.FindGame).
///
/// The disk scan is deliberately not folded in here. This runs before the page can draw
/// anything, and a machine where none of the guesses hit is exactly the machine the scan walks for
/// the longest - home plus every drive, five deep. Folding it in turns "we could not
/// guess" into "the app does not open", with nothing on screen to say why. The scan is
/// `scan_for_game`, run afterwards on a thread with the busy label showing.
#[cfg(windows)]
pub fn find() -> Option<PathBuf> {
    for root in candidate_roots() {
        if is_game(&root) {
            return Some(root);
        }
        let app = root.join("app");
        if is_game(&app) {
            return Some(app);
        }
    }
    None
}

pub fn is_game(app: &Path) -> bool {
    exe(app).is_file()
}

/// macOS: the `.app`'s parent (`Data/`) is GameRootPath.
#[cfg(target_os = "macos")]
pub fn root(app: &Path) -> PathBuf {
    app.parent()
        .map(|p| p.to_path_buf())
        .unwrap_or_else(|| PathBuf::from("."))
}

/// Windows: the folder holding `velocidrone.exe` is the root.
#[cfg(windows)]
pub fn root(app: &Path) -> PathBuf {
    win_root(app)
}

#[cfg(target_os = "macos")]
pub fn exe(app: &Path) -> PathBuf {
    app.join("Contents/MacOS/velocidrone")
}

#[cfg(windows)]
pub fn exe(app: &Path) -> PathBuf {
    win_exe(app)
}

/// macOS: doorstop dylib + preloader. (Official and patched look the same on disk.)
#[cfg(target_os = "macos")]
pub fn has_bepinex(root: &Path) -> bool {
    root.join("libdoorstop.dylib").is_file()
        && root
            .join("BepInEx/core/BepInEx.Preloader.dll")
            .is_file()
}

/// Windows: the winhttp.dll proxy plus the preloader itself.
///
/// Stricter than `GameInstall.HasBepInEx`, which asks only whether a folder named BepInEx
/// exists. That is the weaker half of an invariant macOS already spells out: a marker that
/// outlived the files it describes says the loader is fine while the game has nothing to
/// load, so the install skips it and the one button that could repair the machine reports
/// there is nothing to repair. On Windows an antivirus quarantine of `BepInEx/core`
/// produces exactly that state, and the symptom is a game that starts, draws no captures,
/// and logs nothing.
#[cfg(windows)]
pub fn has_bepinex(root: &Path) -> bool {
    root.join("winhttp.dll").is_file()
        && root.join("BepInEx/core/BepInEx.Preloader.dll").is_file()
}

/// Windows path: `velocidrone.exe` inside the game folder.
pub fn win_exe(game: &Path) -> PathBuf {
    game.join("velocidrone.exe")
}

/// Windows path: the game folder itself is the root (no `.app` parent).
pub fn win_root(game: &Path) -> PathBuf {
    game.to_path_buf()
}

/// Windows named install guesses (GameInstall.NamedRoots), apart from mounted drives.
///
/// There is no default to look up — VelociDrone ships as a zip with no installer — so this
/// is a list of guesses. The first three are what the guide recommends; the two Downloads
/// entries are not — they are where the launcher landed on the machines we have seen.
/// Program Files is deliberately absent: the guide says to stay out of it.
pub fn win_named_roots(home: &Path) -> Vec<PathBuf> {
    vec![
        PathBuf::from(r"C:\VelociDrone"),
        home.join("Desktop").join("VelociDrone"),
        home.join("Documents").join("VelociDrone"),
        home.join("Downloads").join("VelociDrone"),
        home.join("Downloads").join("Velocidrone Windows Launcher"),
    ]
}

#[cfg(windows)]
fn drive_roots() -> Vec<PathBuf> {
    let mut out = Vec::new();
    for letter in b'A'..=b'Z' {
        let root = PathBuf::from(format!("{}:\\", letter as char));
        if root.exists() {
            out.push(root.join("VelociDrone"));
        }
    }
    out
}

#[cfg(windows)]
fn candidate_roots() -> Vec<PathBuf> {
    // With no home the joins yield `Desktop\VelociDrone` relative to whatever the working
    // directory happens to be, and a match there would be saved as the game. `scan_for_game`
    // already drops the home root in that case; this now agrees with it.
    let mut roots = match dirs::home_dir() {
        Some(home) => win_named_roots(&home),
        None => vec![PathBuf::from(r"C:\VelociDrone")],
    };
    roots.extend(drive_roots());
    roots
}

/// The fallback walk, for when every guess missed (GameInstall.ScanForGame).
///
/// A walk rather than a lookup because there is nothing to look up: PatchKit records the
/// install path in no registry key, no uninstall entry, and not in its own `%LOCALAPPDATA%`
/// folder, which holds one 32-byte id and nothing else — checked on a real install. So when
/// the guesses miss, the choice is this or asking the person to go and find it themselves.
#[cfg(windows)]
pub fn scan_for_game(log: &mut dyn FnMut(String)) -> Option<PathBuf> {
    let mut roots = Vec::new();
    if let Some(home) = dirs::home_dir() {
        roots.push(home);
    }
    for letter in b'A'..=b'Z' {
        let root = PathBuf::from(format!("{}:\\", letter as char));
        if root.exists() {
            roots.push(root);
        }
    }
    scan_roots(roots, 5, log)
}

/// Bounded BFS for `velocidrone.exe` (GameInstall.ScanRoots).
///
/// A junction can point back up the tree and make this walk forever — reparse points are
/// skipped on Windows. Unreadable folders are not where the game is.
pub fn scan_roots(
    roots: impl IntoIterator<Item = PathBuf>,
    max_depth: u32,
    log: &mut dyn FnMut(String),
) -> Option<PathBuf> {
    // Lowercased on both sides: the C# builds this set with OrdinalIgnoreCase, and Windows
    // keeps whatever case created a directory — a `windows\` or `programdata\` restored
    // from an archive is the same folder and has to be skipped the same way.
    let skip: HashSet<&str> = [
        "windows",
        "$recycle.bin",
        "system volume information",
        "programdata",
        "appdata",
        "node_modules",
        ".git",
        "windowsapps",
    ]
    .into_iter()
    .collect();

    for root in roots {
        log(format!("looking under {}", root.display()));
        let mut queue: VecDeque<(PathBuf, u32)> = VecDeque::new();
        queue.push_back((root, 0));
        while let Some((dir, depth)) = queue.pop_front() {
            if dir.join("velocidrone.exe").is_file() {
                log(format!("found {}", dir.display()));
                return Some(dir);
            }
            if depth >= max_depth {
                continue;
            }
            let entries = match fs::read_dir(&dir) {
                Ok(e) => e,
                Err(_) => continue,
            };
            for entry in entries.flatten() {
                let path = entry.path();
                if !path.is_dir() {
                    continue;
                }
                // Lossy, not rejected: the name is only ever used for the two comparisons
                // below, and a lossy one never accidentally equals `windows` or starts with
                // a dot. Windows filenames are UTF-16 and may hold unpaired surrogates, so
                // `into_string` rejecting one dropped that whole subtree — a game installed
                // under it was unfindable and the scan said it was not on any disk.
                let name = entry.file_name().to_string_lossy().into_owned();
                if name.starts_with('.') || skip.contains(name.to_ascii_lowercase().as_str()) {
                    continue;
                }
                if is_reparse_point(&path) {
                    continue;
                }
                queue.push_back((path, depth + 1));
            }
        }
    }
    log("velocidrone.exe is not on any fixed disk".into());
    None
}

/// `symlink_metadata`, not `metadata`: the latter follows the reparse point and reports the
/// target's attributes, which never carry the bit — so the guard read as "not a junction"
/// for every junction. Windows ships several whose names are not in the skip list
/// (`C:\Users\All Users` to ProgramData, `C:\Documents and Settings` to `C:\Users`), so the
/// walk descended into the trees the skip list exists to avoid and covered `C:\Users` twice.
/// The depth cap kept it finite, not cheap. The C# never had this: `DirectoryInfo`'s
/// attributes do not follow.
fn is_reparse_point(path: &Path) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
        fs::symlink_metadata(path)
            .map(|m| m.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0)
            .unwrap_or(false)
    }
    #[cfg(not(windows))]
    {
        let _ = path;
        false
    }
}

pub fn dll_file_version(dll: &Path) -> Option<String> {
    let bytes = fs::read(dll).ok()?;
    version_from_pe(&bytes)
}

fn version_from_pe(bytes: &[u8]) -> Option<String> {
    if let Some(v) = try_pe64_version(bytes) {
        return Some(v);
    }
    try_pe32_version(bytes)
}

fn format_fixed(fixed: &pelite::image::VS_FIXEDFILEINFO) -> String {
    let v = &fixed.dwFileVersion;
    format!("{}.{}.{}.{}", v.Major, v.Minor, v.Patch, v.Build)
}

fn try_pe64_version(bytes: &[u8]) -> Option<String> {
    use pelite::pe64::Pe;
    let pe = pelite::pe64::PeFile::from_bytes(bytes).ok()?;
    let resources = pe.resources().ok()?;
    let info = resources.version_info().ok()?;
    info.fixed().map(format_fixed)
}

fn try_pe32_version(bytes: &[u8]) -> Option<String> {
    use pelite::pe32::Pe;
    let pe = pelite::pe32::PeFile::from_bytes(bytes).ok()?;
    let resources = pe.resources().ok()?;
    let info = resources.version_info().ok()?;
    info.fixed().map(format_fixed)
}

pub fn installed_mod_version(root: &Path) -> Option<String> {
    let dll = root.join("BepInEx/plugins/VDGS.dll");
    if !dll.is_file() {
        return None;
    }
    // Truncated / unparseable DLL = absent, so state reports "the mod" as missing.
    dll_file_version(&dll)
}

pub fn bundled_mod_dir(resource_dir: &Path) -> Option<PathBuf> {
    let dir = resource_dir.join("mod");
    if dir.join("BepInEx/plugins/VDGS.dll").is_file() {
        Some(dir)
    } else {
        None
    }
}

pub fn bundled_mod_version(resource_dir: &Path) -> Option<String> {
    let dir = bundled_mod_dir(resource_dir)?;
    dll_file_version(&dir.join("BepInEx/plugins/VDGS.dll"))
}

#[derive(Clone, Debug, Serialize)]
pub struct SceneInfo {
    pub name: String,
    pub splats: u64,
    pub collision: bool,
    pub bytes: u64,
    pub converted: bool,
    /// meta.json's `revision`; 1 when absent, which every capture cut before the field
    /// existed is. Compared against the catalog's to offer an update.
    pub revision: u64,
}

pub fn scenes(root: &Path) -> Vec<SceneInfo> {
    let mut found = Vec::new();
    let vdgs = root.join("vdgs");
    if !vdgs.is_dir() {
        return found;
    }

    let mut seen: HashSet<String> = HashSet::new();

    if let Ok(entries) = fs::read_dir(&vdgs) {
        for entry in entries.flatten() {
            let dir = entry.path();
            if !dir.is_dir() {
                continue;
            }
            let meta = dir.join("meta.json");
            if !meta.is_file() {
                continue;
            }
            let name = match entry.file_name().into_string() {
                Ok(n) => n,
                Err(_) => continue,
            };
            // .<name>.new / .<name>.old are this app's own staging and backup folders.
            if name.starts_with('.') {
                continue;
            }
            seen.insert(name.to_ascii_lowercase());
            found.push(SceneInfo {
                name,
                splats: splat_count(&meta),
                collision: dir.join("collision.bin").is_file(),
                bytes: directory_size(&dir),
                converted: true,
                revision: meta_revision(&meta),
            });
        }
    }

    if let Ok(entries) = fs::read_dir(&vdgs) {
        for entry in entries.flatten() {
            let ply = entry.path();
            if !ply.is_file() {
                continue;
            }
            let name_os = entry.file_name();
            let name_str = match name_os.to_str() {
                Some(s) => s,
                None => continue,
            };
            if !name_str.to_ascii_lowercase().ends_with(".ply") {
                continue;
            }
            let name = &name_str[..name_str.len() - 4];
            if !seen.insert(name.to_ascii_lowercase()) {
                continue;
            }
            let mut bytes = 0u64;
            for ext in [".ply", ".collision.bin", ".placement.json"] {
                let p = vdgs.join(format!("{name}{ext}"));
                if let Ok(meta) = fs::metadata(&p) {
                    bytes += meta.len();
                }
            }
            found.push(SceneInfo {
                name: name.to_string(),
                splats: ply_vertex_count(&ply),
                collision: vdgs.join(format!("{name}.collision.bin")).is_file(),
                bytes,
                converted: false,
                revision: 1,
            });
        }
    }

    found.sort_by(|a, b| a.name.to_ascii_lowercase().cmp(&b.name.to_ascii_lowercase()));
    found
}

fn meta_revision(meta_path: &Path) -> u64 {
    fs::read_to_string(meta_path)
        .ok()
        .and_then(|t| serde_json::from_str::<serde_json::Value>(&t).ok())
        .and_then(|v| v.get("revision").and_then(|n| n.as_u64()))
        .unwrap_or(1)
}

fn splat_count(meta_path: &Path) -> u64 {
    let text = match fs::read_to_string(meta_path) {
        Ok(t) => t,
        Err(_) => return 0,
    };
    let v: serde_json::Value = match serde_json::from_str(&text) {
        Ok(v) => v,
        Err(_) => return 0,
    };
    v.get("splatCount")
        .and_then(|n| n.as_u64())
        .or_else(|| v.get("splatCount").and_then(|n| n.as_i64()).map(|n| n as u64))
        .unwrap_or(0)
}

fn ply_vertex_count(path: &Path) -> u64 {
    let file = match File::open(path) {
        Ok(f) => f,
        Err(_) => return 0,
    };
    let reader = BufReader::new(file);
    for line in reader.lines() {
        let line = match line {
            Ok(l) => l,
            Err(_) => return 0,
        };
        if line.starts_with("end_header") {
            break;
        }
        const PREFIX: &str = "element vertex ";
        if let Some(rest) = line.strip_prefix(PREFIX) {
            if let Ok(n) = rest.trim().parse::<u64>() {
                return n;
            }
        }
    }
    0
}

fn directory_size(dir: &Path) -> u64 {
    let entries = match fs::read_dir(dir) {
        Ok(e) => e,
        Err(_) => return 0,
    };
    let mut total = 0u64;
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_file() {
            if let Ok(meta) = fs::metadata(&path) {
                total += meta.len();
            }
        }
    }
    total
}

pub type Bindings = BTreeMap<String, Vec<String>>;

/// Display path: missing or unreadable bindings read as empty (companion ReadBindings).
pub fn read_bindings(root: &Path) -> Bindings {
    try_read_bindings(root).unwrap_or_default()
}

/// Bind/unbind path: absent file is Ok(empty); present-but-unparseable is Err so a
/// corrupt bindings.json is never overwritten with an empty map (companion Bind).
pub fn try_read_bindings(root: &Path) -> io::Result<Bindings> {
    let path = root.join("vdgs/bindings.json");
    match fs::read_to_string(&path) {
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(Bindings::new()),
        Err(e) => Err(e),
        Ok(text) => try_parse_bindings(&text),
    }
}


fn bad_bindings(why: &str) -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        format!("vdgs/bindings.json: {why}"),
    )
}

fn try_parse_bindings(text: &str) -> io::Result<Bindings> {
    let mut map = Bindings::new();
    if text.trim().is_empty() {
        return Ok(map);
    }
    let v: serde_json::Value = serde_json::from_str(text)
        .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
    // Valid JSON of the wrong shape is refused rather than normalised. Reading an array,
    // a string, or a track whose value is not a list of names as "no bindings" and then
    // saving that is the same loss as failing to parse at all - the file is only ever
    // written by this app and the mod, so a shape that is not an object of string arrays
    // means it was edited by hand or damaged, and neither is ours to tidy away.
    let obj = v
        .as_object()
        .ok_or_else(|| bad_bindings("the top level is not an object"))?;
    for (k, val) in obj {
        let arr = val
            .as_array()
            .ok_or_else(|| bad_bindings(&format!("\"{k}\" is not a list of capture names")))?;
        let mut scenes = Vec::new();
        for item in arr {
            let s = item
                .as_str()
                .ok_or_else(|| bad_bindings(&format!("\"{k}\" holds something that is not a name")))?;
            scenes.push(s.to_string());
        }
        map.insert(k.clone(), scenes);
    }
    Ok(map)
}

pub fn write_bindings(root: &Path, b: &Bindings) -> io::Result<()> {
    let path = root.join("vdgs/bindings.json");
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let text = write_bindings_string(b);
    fs::write(path, text)
}

fn write_bindings_string(b: &Bindings) -> String {
    // Pretty JSON, 2-space — same shape as companion/Json.WriteBindings.
    let mut out = String::from("{\n");
    let mut first = true;
    for (k, scenes) in b {
        if !first {
            out.push_str(",\n");
        }
        first = false;
        out.push_str("  ");
        out.push_str(&serde_json::to_string(k).unwrap_or_else(|_| format!("{:?}", k)));
        out.push_str(": [\n");
        for (i, s) in scenes.iter().enumerate() {
            out.push_str("    ");
            out.push_str(&serde_json::to_string(s).unwrap_or_else(|_| format!("{:?}", s)));
            if i + 1 < scenes.len() {
                out.push(',');
            }
            out.push('\n');
        }
        out.push_str("  ]");
    }
    if !b.is_empty() {
        out.push('\n');
    }
    out.push('}');
    out
}

/// Binds `track` to `scene`. Returns false when the list was left as it was.
///
/// A first install replaces whatever the track pointed at - the capture just installed
/// is what the person asked to see. Anything after that (`updating`) leaves the track's
/// list alone: a second capture they added, or a capture of their own they pointed the
/// track at instead, is their choice and outlives a re-download of ours.
pub fn bind(root: &Path, track: &str, scene: &str, updating: bool) -> io::Result<bool> {
    let mut map = try_read_bindings(root)?;
    let existing = map.get(track);
    if existing.is_some_and(|list| list.iter().any(|s| s == scene))
        || (updating && existing.is_some_and(|list| !list.is_empty()))
    {
        return Ok(false);
    }
    map.insert(track.to_string(), vec![scene.to_string()]);
    write_bindings(root, &map)?;
    Ok(true)
}

pub fn unbind(root: &Path, track: &str) -> io::Result<bool> {
    let path = root.join("vdgs/bindings.json");
    if !path.is_file() {
        return Ok(false);
    }
    let mut map = try_read_bindings(root)?;
    if map.remove(track).is_none() {
        return Ok(false);
    }
    write_bindings(root, &map)?;
    Ok(true)
}

pub fn install_archive(
    root: &Path,
    zip: &Path,
    label: &str,
    log: &mut dyn FnMut(String),
) -> Result<(), catalog::Error> {
    if crate::launch::is_running() {
        return Err(catalog::Error::Msg(
            "VelociDrone is running. Close it first - files in use cannot be replaced.".into(),
        ));
    }
    let carries_ui = zip_carries_ui(zip)?;
    let written = catalog::extract(zip, root, &["placement.json", "bindings.json"], log)?;
    if carries_ui {
        sweep_interface(root, &written, log);
    }
    log(format!("installed {label}"));
    Ok(())
}

/// Installs one capture archive as `vdgs/<install_as>/`, replacing whatever is there.
///
/// The archive is unpacked beside the target and swapped in with a rename, so a
/// failure part-way leaves the old folder untouched, and a file the new cut no longer
/// ships (a stale chunk.bin - the trap ARCHITECTURE warns about) does not survive from
/// the old one. Only the user's placement.json is carried over; bindings.json lives
/// outside the folder and is never touched.
pub fn install_capture_archive(
    root: &Path,
    zip: &Path,
    install_as: &str,
    log: &mut dyn FnMut(String),
) -> Result<(), catalog::Error> {
    if crate::launch::is_running() {
        return Err(catalog::Error::Msg(
            "VelociDrone is running. Close it first - files in use cannot be replaced.".into(),
        ));
    }
    // The name comes from the catalog and ends up in remove_dir_all: one plain folder
    // name, nothing that could walk out of vdgs/.
    if !is_plain_folder_name(install_as) {
        return Err(catalog::Error::Msg(format!(
            "refusing to install as {install_as:?}: not a plain folder name"
        )));
    }
    let vdgs = root.join("vdgs");
    let target = vdgs.join(install_as);
    let staging = vdgs.join(format!(".{install_as}.new"));
    let retired = vdgs.join(format!(".{install_as}.old"));
    // A previous run that died between the two renames left only the backup: put it back
    // before anything else. A backup beside a live folder is one whose cleanup failed.
    let step = |what: String, e: io::Error| catalog::Error::Msg(format!("{what}: {e}"));
    if retired.exists() && !target.exists() {
        fs::rename(&retired, &target)
            .map_err(|e| step(format!("restoring {} to {}", retired.display(), target.display()), e))?;
        log(format!("restored {install_as} from an interrupted update"));
    }
    // Leftovers are removed, not unpacked over: a stale file in the staging area would
    // ride into the new folder, which is the trap this swap exists to close.
    if staging.exists() {
        fs::remove_dir_all(&staging)
            .map_err(|e| step(format!("removing the old staging folder {}", staging.display()), e))?;
    }
    fs::create_dir_all(&staging)
        .map_err(|e| step(format!("creating {}", staging.display()), e))?;

    let result = (|| -> Result<(), catalog::Error> {
        catalog::extract(zip, &staging, &[], log)?;
        let unpacked = staging.join("vdgs").join(install_as);
        if !unpacked.join("meta.json").is_file() {
            return Err(catalog::Error::Msg(format!(
                "the archive does not carry vdgs/{install_as}/meta.json"
            )));
        }
        let placement = target.join("placement.json");
        if placement.is_file() {
            fs::copy(&placement, unpacked.join("placement.json"))
                .map_err(|e| step(format!("keeping your {}", placement.display()), e))?;
            log("kept your placement.json".into());
        }
        if target.exists() {
            if retired.exists() {
                fs::remove_dir_all(&retired)
                    .map_err(|e| step(format!("removing the old backup {}", retired.display()), e))?;
            }
            fs::rename(&target, &retired)
                .map_err(|e| step(format!("moving {} aside to {}", target.display(), retired.display()), e))?;
        }
        if let Err(e) = fs::rename(&unpacked, &target) {
            let what = format!("putting the new {install_as} in place ({} -> {})", unpacked.display(), target.display());
            if retired.exists() {
                if let Err(back) = fs::rename(&retired, &target) {
                    return Err(catalog::Error::Msg(format!(
                        "{what}: {e}; and the previous one could not be put back ({back}). \
                         Your previous files, placement.json included, are in {} - rename that folder to {} by hand.",
                        retired.display(), target.display()
                    )));
                }
            }
            return Err(step(what, e));
        }
        Ok(())
    })();
    if let Err(e) = fs::remove_dir_all(&staging) {
        if staging.exists() {
            log(format!("could not remove {}: {e} - delete it by hand", staging.display()));
        }
    }
    result?;
    // Only a completed swap retires the backup. A backup that will not go is said out
    // loud: it is a full copy of a capture, and the game would list it as one.
    if let Err(e) = fs::remove_dir_all(&retired) {
        if retired.exists() {
            log(format!("could not remove the old copy {}: {e} - delete it by hand", retired.display()));
        }
    }
    log(format!("installed {install_as}"));
    Ok(())
}

/// One path component with no way out of its parent: what a catalog `installAs` and a
/// capture folder name must be.
pub fn is_plain_folder_name(name: &str) -> bool {
    !name.is_empty()
        && name != "."
        && name != ".."
        && !name.starts_with('.')
        && !name.contains(['/', '\\', ':', '\0'])
}

fn zip_carries_ui(zip: &Path) -> Result<bool, catalog::Error> {
    let file = File::open(zip)?;
    let mut archive = zip::ZipArchive::new(file)?;
    for i in 0..archive.len() {
        let entry = archive.by_index(i)?;
        let name = entry.name().replace('\\', "/");
        if name.to_ascii_lowercase().starts_with("vdgs/ui/") {
            return Ok(true);
        }
    }
    Ok(false)
}

pub fn install_bundled_mod(
    root: &Path,
    mod_dir: &Path,
    log: &mut dyn FnMut(String),
) -> io::Result<()> {
    if crate::launch::is_running() {
        return Err(io::Error::new(
            io::ErrorKind::Other,
            "VelociDrone is running. Close it first - files in use cannot be replaced.",
        ));
    }
    let src = fs::canonicalize(mod_dir).unwrap_or_else(|_| mod_dir.to_path_buf());
    let mut copied = 0usize;
    let mut written: Vec<PathBuf> = Vec::new();

    for file in list_files_recursive(&src)? {
        let relative = match file.strip_prefix(&src) {
            Ok(r) => r,
            Err(_) => continue,
        };
        let rel_str = relative.to_string_lossy();
        // README.txt (and anything else) at the payload top is for a person, not the game.
        if !rel_str.contains('/') && !rel_str.contains('\\') {
            continue;
        }

        let target = root.join(relative);
        if keep_existing(&target, log) {
            continue;
        }
        if let Some(parent) = target.parent() {
            fs::create_dir_all(parent)?;
        }
        fs::copy(&file, &target)?;
        written.push(fs::canonicalize(&target).unwrap_or_else(|_| target.clone()));
        copied += 1;
    }
    sweep_interface(root, &written, log);
    let ver = dll_file_version(&mod_dir.join("BepInEx/plugins/VDGS.dll"))
        .unwrap_or_else(|| "?".into());
    log(format!("installed mod {ver} ({copied} files)"));
    Ok(())
}

fn list_files_recursive(dir: &Path) -> io::Result<Vec<PathBuf>> {
    let mut out = Vec::new();
    let mut stack = vec![dir.to_path_buf()];
    while let Some(d) = stack.pop() {
        for entry in fs::read_dir(&d)? {
            let entry = entry?;
            let path = entry.path();
            if path.is_dir() {
                stack.push(path);
            } else {
                out.push(path);
            }
        }
    }
    Ok(out)
}

fn keep_existing(target: &Path, log: &mut dyn FnMut(String)) -> bool {
    if !target.is_file() {
        return false;
    }
    let leaf = match target.file_name().and_then(|s| s.to_str()) {
        Some(s) => s,
        None => return false,
    };
    if !leaf.eq_ignore_ascii_case("placement.json") && !leaf.eq_ignore_ascii_case("bindings.json")
    {
        return false;
    }
    log(format!("kept your {leaf}"));
    true
}

fn sweep_interface(root: &Path, written: &[PathBuf], log: &mut dyn FnMut(String)) {
    let ui = root.join("vdgs/ui");
    if !ui.is_dir() {
        return;
    }

    let keep: HashSet<PathBuf> = written
        .iter()
        .map(|p| fs::canonicalize(p).unwrap_or_else(|_| p.clone()))
        .collect();

    let ui_abs = fs::canonicalize(&ui).unwrap_or_else(|_| ui.clone());
    if !keep.iter().any(|k| k.starts_with(&ui_abs)) {
        log("no interface in this payload - left the one already there".into());
        return;
    }

    let mut dropped = 0usize;
    let files = match list_files_recursive(&ui) {
        Ok(f) => f,
        Err(_) => return,
    };
    for f in files {
        let abs = fs::canonicalize(&f).unwrap_or_else(|_| f.clone());
        if keep.contains(&abs) {
            continue;
        }
        match fs::remove_file(&f) {
            Ok(()) => dropped += 1,
            Err(ex) => {
                let name = f
                    .file_name()
                    .and_then(|s| s.to_str())
                    .unwrap_or("?");
                log(format!("could not remove {name}: {ex}"));
            }
        }
    }
    if dropped > 0 {
        log(format!("dropped {dropped} file(s) from an older interface"));
    }
}

pub fn uninstall_mod(root: &Path, log: &mut dyn FnMut(String)) -> io::Result<()> {
    if crate::launch::is_running() {
        return Err(io::Error::new(
            io::ErrorKind::Other,
            "VelociDrone is running. Close it first - files in use cannot be removed.",
        ));
    }
    let mut removed = 0usize;
    for rel in ["BepInEx/plugins/VDGS.dll", "vdgs/vdgs-shaders"] {
        let path = root.join(rel);
        if !path.is_file() {
            continue;
        }
        fs::remove_file(&path)?;
        log(format!("removed {rel}"));
        removed += 1;
    }

    let ui = root.join("vdgs/ui");
    if ui.is_dir() {
        fs::remove_dir_all(&ui)?;
        log("removed vdgs/ui".into());
        removed += 1;
    }

    log(if removed == 0 {
        "nothing to remove".into()
    } else {
        "the mod is off; captures kept".into()
    });
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    #[test]
    fn scenes_lists_dirs_and_plys_dir_wins() {
        let root = tmp();
        let v = root.join("vdgs");
        std::fs::create_dir_all(v.join("a")).unwrap();
        std::fs::write(v.join("a/meta.json"), r#"{"splatCount": 12, "chunkCount": 1}"#).unwrap();
        std::fs::write(v.join("a/collision.bin"), b"xx").unwrap();
        std::fs::write(
            v.join("a.ply"),
            b"ply\nformat binary_little_endian 1.0\nelement vertex 99\nend_header\n",
        )
        .unwrap();
        std::fs::write(v.join("b.ply"), b"ply\nelement vertex 7\nend_header\n").unwrap();
        let s = scenes(&root);
        assert_eq!(
            s.iter().map(|x| x.name.as_str()).collect::<Vec<_>>(),
            ["a", "b"]
        );
        assert!(s[0].converted && s[0].collision && s[0].splats == 12);
        assert!(!s[1].converted && s[1].splats == 7);
        // No revision key, and a bare .ply, both read as the first cut.
        assert_eq!((s[0].revision, s[1].revision), (1, 1));
    }

    #[test]
    fn scenes_skip_the_apps_own_dot_folders() {
        let root = tmp();
        let v = root.join("vdgs");
        for d in ["X", ".X.old", ".X.new"] {
            std::fs::create_dir_all(v.join(d)).unwrap();
            std::fs::write(v.join(d).join("meta.json"), r#"{"splatCount": 1, "chunkCount": 1}"#).unwrap();
        }
        assert_eq!(scenes(&root).iter().map(|s| s.name.as_str()).collect::<Vec<_>>(), ["X"]);
    }

    #[test]
    fn scenes_read_revision_from_meta() {
        let root = tmp();
        let v = root.join("vdgs");
        std::fs::create_dir_all(v.join("r2")).unwrap();
        std::fs::write(v.join("r2/meta.json"), r#"{"splatCount": 1, "chunkCount": 1, "revision": 2}"#).unwrap();
        assert_eq!(scenes(&root)[0].revision, 2);
    }

    #[test]
    fn bindings_roundtrip_and_unbind() {
        let root = tmp();
        bind(&root, "VDGS X", "x-dir", false).unwrap();
        bind(&root, "VDGS Y", "y-dir", false).unwrap();
        let b = read_bindings(&root);
        assert_eq!(b["VDGS X"], vec!["x-dir"]);
        assert_eq!(b.len(), 2);
        assert!(unbind(&root, "VDGS X").unwrap());
        assert!(!unbind(&root, "VDGS X").unwrap());
        assert_eq!(read_bindings(&root).len(), 1);
    }

    #[test]
    fn install_capture_archive_replaces_the_folder_and_keeps_placement() {
        let root = tmp();
        let zip_path = root.join("cap.zip");
        {
            let f = std::fs::File::create(&zip_path).unwrap();
            let mut w = zip::ZipWriter::new(f);
            let o = zip::write::SimpleFileOptions::default();
            for (name, body) in [
                ("vdgs/X/meta.json", b"{\"revision\":2}".as_slice()),
                ("vdgs/X/sh.bin", b"new-sh"),
                ("vdgs/X/placement.json", b"shipped"),
                ("README.txt", b"note"),
            ] {
                w.start_file(name, o).unwrap();
                std::io::Write::write_all(&mut w, body).unwrap();
            }
            w.finish().unwrap();
        }
        let x = root.join("vdgs/X");
        std::fs::create_dir_all(&x).unwrap();
        std::fs::write(x.join("meta.json"), b"{}").unwrap();
        std::fs::write(x.join("sh.bin"), b"old-sh").unwrap();
        std::fs::write(x.join("chunk.bin"), b"stale").unwrap();
        std::fs::write(x.join("placement.json"), b"mine").unwrap();
        std::fs::write(root.join("vdgs/bindings.json"), b"{\"T\":[\"X\"]}").unwrap();
        let mut log = |_s: String| {};
        install_capture_archive(&root, &zip_path, "X", &mut log).unwrap();
        assert_eq!(std::fs::read(x.join("sh.bin")).unwrap(), b"new-sh");
        assert!(!x.join("chunk.bin").exists(), "a file the new cut does not ship must go");
        assert_eq!(std::fs::read(x.join("placement.json")).unwrap(), b"mine");
        assert_eq!(std::fs::read(root.join("vdgs/bindings.json")).unwrap(), b"{\"T\":[\"X\"]}");
        assert!(!root.join("README.txt").exists());
        assert!(!root.join("vdgs/.X.new").exists() && !root.join("vdgs/.X.old").exists());
    }

    #[test]
    fn install_capture_archive_refuses_a_name_that_is_not_a_folder() {
        let root = tmp();
        let zip_path = root.join("cap.zip");
        std::fs::write(&zip_path, b"not even a zip").unwrap();
        let mut log = |_s: String| {};
        for bad in ["../x", "a/b", "..", ".", "", ".hidden", "a\\b"] {
            assert!(install_capture_archive(&root, &zip_path, bad, &mut log).is_err(), "{bad:?}");
        }
        assert!(!root.join("vdgs").exists(), "nothing may be created for a refused name");
    }

    #[test]
    fn install_capture_archive_restores_a_backup_left_by_an_interrupted_update() {
        let root = tmp();
        let zip_path = root.join("bad.zip");
        std::fs::write(&zip_path, b"not a zip").unwrap();
        let old = root.join("vdgs/.X.old");
        std::fs::create_dir_all(&old).unwrap();
        std::fs::write(old.join("sh.bin"), b"old-sh").unwrap();
        let mut log = |_s: String| {};
        // The archive is unreadable, so this run fails - but the backup is back in place.
        assert!(install_capture_archive(&root, &zip_path, "X", &mut log).is_err());
        assert_eq!(std::fs::read(root.join("vdgs/X/sh.bin")).unwrap(), b"old-sh");
        assert!(!old.exists());
    }

    #[test]
    fn install_capture_archive_refuses_an_archive_without_the_folder() {
        let root = tmp();
        let zip_path = root.join("bad.zip");
        {
            let f = std::fs::File::create(&zip_path).unwrap();
            let mut w = zip::ZipWriter::new(f);
            w.start_file("vdgs/Y/meta.json", zip::write::SimpleFileOptions::default()).unwrap();
            std::io::Write::write_all(&mut w, b"{}").unwrap();
            w.finish().unwrap();
        }
        let x = root.join("vdgs/X");
        std::fs::create_dir_all(&x).unwrap();
        std::fs::write(x.join("sh.bin"), b"old-sh").unwrap();
        let mut log = |_s: String| {};
        assert!(install_capture_archive(&root, &zip_path, "X", &mut log).is_err());
        // The old folder is untouched and nothing is left behind.
        assert_eq!(std::fs::read(x.join("sh.bin")).unwrap(), b"old-sh");
        assert!(!root.join("vdgs/.X.new").exists() && !root.join("vdgs/Y").exists());
    }

    #[test]
    fn bind_keeps_the_users_list_on_update() {
        let root = tmp();
        let path = root.join("vdgs/bindings.json");
        assert!(bind(&root, "VDGS X", "x-dir", false).unwrap());
        // A second capture the user added survives a re-bind of the same capture.
        std::fs::write(&path, br#"{"VDGS X": ["x-dir", "extra"]}"#).unwrap();
        assert!(!bind(&root, "VDGS X", "x-dir", true).unwrap());
        assert_eq!(read_bindings(&root)["VDGS X"], vec!["x-dir", "extra"]);
        // So does a track the user pointed at their own capture instead of ours.
        std::fs::write(&path, br#"{"VDGS X": ["mine"]}"#).unwrap();
        assert!(!bind(&root, "VDGS X", "x-dir", true).unwrap());
        assert_eq!(read_bindings(&root)["VDGS X"], vec!["mine"]);
        // A first install still replaces: the capture just installed is what was asked for.
        assert!(bind(&root, "VDGS X", "x-dir", false).unwrap());
        assert_eq!(read_bindings(&root)["VDGS X"], vec!["x-dir"]);
    }

    #[test]
    fn bind_refuses_corrupt_bindings_and_leaves_file() {
        let root = tmp();
        let path = root.join("vdgs/bindings.json");
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        let corrupt = b"{\"Other\":[\"scene\"]\nnot-json";
        std::fs::write(&path, corrupt).unwrap();
        let before = std::fs::read(&path).unwrap();
        assert!(bind(&root, "VDGS X", "x-dir", false).is_err());
        assert_eq!(std::fs::read(&path).unwrap(), before);
        assert_eq!(before, corrupt);
    }

    #[test]
    fn installed_mod_version_none_for_non_pe_dll() {
        let root = tmp();
        let dll = root.join("BepInEx/plugins/VDGS.dll");
        std::fs::create_dir_all(dll.parent().unwrap()).unwrap();
        std::fs::write(&dll, b"not a PE file").unwrap();
        assert!(installed_mod_version(&root).is_none());
    }

    #[test]
    fn install_bundled_mod_sweeps_old_ui_and_keeps_bindings() {
        let root = tmp();
        let payload = tmp();
        for f in [
            "BepInEx/plugins/VDGS.dll",
            "vdgs/vdgs-shaders",
            "vdgs/ui/index.html",
            "vdgs/ui/assets/new.js",
        ] {
            let p = payload.join(f);
            std::fs::create_dir_all(p.parent().unwrap()).unwrap();
            std::fs::write(&p, b"n").unwrap();
        }
        std::fs::write(payload.join("README.txt"), b"top").unwrap();
        std::fs::create_dir_all(root.join("vdgs/ui/assets")).unwrap();
        std::fs::write(root.join("vdgs/ui/assets/old.js"), b"o").unwrap();
        std::fs::write(root.join("vdgs/bindings.json"), b"{\"K\":[\"v\"]}").unwrap();
        let mut log = |_s: String| {};
        install_bundled_mod(&root, &payload, &mut log).unwrap();
        assert!(!root.join("vdgs/ui/assets/old.js").exists());
        assert!(root.join("vdgs/ui/assets/new.js").exists());
        assert!(!root.join("README.txt").exists());
        assert_eq!(
            std::fs::read(root.join("vdgs/bindings.json")).unwrap(),
            b"{\"K\":[\"v\"]}"
        );
        uninstall_mod(&root, &mut log).unwrap();
        assert!(
            !root.join("BepInEx/plugins/VDGS.dll").exists() && !root.join("vdgs/ui").exists()
        );
        assert!(root.join("vdgs/bindings.json").exists());
    }

    fn tmp() -> PathBuf {
        static N: AtomicU32 = AtomicU32::new(0);
        let p = std::env::temp_dir().join(format!(
            "vdgs-game-{}-{}",
            std::process::id(),
            N.fetch_add(1, Ordering::SeqCst)
        ));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p).unwrap();
        p
    }

    #[test]
    fn a_bindings_file_of_the_wrong_shape_is_refused_not_emptied() {
        for bad in [
            "[]",
            "\"nope\"",
            "{\"T\": \"one-capture\"}",
            "{\"T\": [1]}",
            "{oops",
        ] {
            let root = tmp();
            bind(&root, "Existing", "cap-a", false).unwrap();
            let path = root.join("vdgs/bindings.json");
            std::fs::write(&path, bad).unwrap();
            assert!(
                bind(&root, "New", "cap-b", false).is_err(),
                "bind should refuse {bad}"
            );
            assert_eq!(
                std::fs::read_to_string(&path).unwrap(),
                bad,
                "the file must be left exactly as it was"
            );
            assert!(unbind(&root, "Existing").is_err(), "unbind should refuse {bad}");
        }
    }

    #[test]
    fn win_exe_and_root_are_the_folder_itself() {
        let g = PathBuf::from("D:/Games/VelociDrone/app");
        assert_eq!(win_exe(&g), g.join("velocidrone.exe"));
        assert_eq!(win_root(&g), g);
    }

    #[test]
    fn win_named_roots_match_the_guide() {
        let home = Path::new("/Users/player");
        let roots = win_named_roots(home);
        assert_eq!(roots[0], PathBuf::from(r"C:\VelociDrone"));
        assert_eq!(roots[1], home.join("Desktop").join("VelociDrone"));
        assert_eq!(roots[2], home.join("Documents").join("VelociDrone"));
        assert_eq!(roots[3], home.join("Downloads").join("VelociDrone"));
        assert_eq!(
            roots[4],
            home.join("Downloads").join("Velocidrone Windows Launcher")
        );
        assert_eq!(roots.len(), 5);
    }

    #[test]
    fn scan_roots_finds_exe_skips_named_dirs_respects_depth() {
        let deep_base = tmp();
        // Too deep for max_depth 1 from base: base/a/b/velocidrone.exe
        let deep = deep_base.join("a").join("b");
        std::fs::create_dir_all(&deep).unwrap();
        std::fs::write(deep.join("velocidrone.exe"), b"x").unwrap();
        let mut log = |_s: String| {};
        assert!(scan_roots([deep_base], 1, &mut log).is_none());

        let base = tmp();
        // Skip list: a game under AppData must not be returned.
        let skip_dir = base.join("AppData").join("hidden");
        std::fs::create_dir_all(&skip_dir).unwrap();
        std::fs::write(skip_dir.join("velocidrone.exe"), b"x").unwrap();
        let hit = base.join("Games").join("VD");
        std::fs::create_dir_all(&hit).unwrap();
        std::fs::write(hit.join("velocidrone.exe"), b"x").unwrap();
        let found = scan_roots([base], 5, &mut log).unwrap();
        assert_eq!(found, hit);
    }
}
