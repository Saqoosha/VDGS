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
/// The disk scan is deliberately not folded in here. This runs before the window exists,
/// and a machine where none of the guesses hit is exactly the machine the scan walks for
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

/// Windows: winhttp.dll proxy + BepInEx directory (GameInstall.HasBepInEx).
#[cfg(windows)]
pub fn has_bepinex(root: &Path) -> bool {
    root.join("winhttp.dll").is_file() && root.join("BepInEx").is_dir()
}

/// Windows path: `velocidrone.exe` beside the game folder.
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
/// is a list of the locations the guide recommends. Program Files is deliberately absent.
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
    let home = dirs::home_dir().unwrap_or_default();
    let mut roots = win_named_roots(&home);
    roots.extend(drive_roots());
    roots
}

/// The fallback walk, for when every guess missed (GameInstall.ScanForGame).
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
    let skip: HashSet<&str> = [
        "Windows",
        "$Recycle.Bin",
        "System Volume Information",
        "ProgramData",
        "AppData",
        "node_modules",
        ".git",
        "WindowsApps",
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
                let name = match entry.file_name().into_string() {
                    Ok(n) => n,
                    Err(_) => continue,
                };
                if name.starts_with('.') || skip.contains(name.as_str()) {
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

fn is_reparse_point(path: &Path) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
        fs::metadata(path)
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
            seen.insert(name.to_ascii_lowercase());
            found.push(SceneInfo {
                name,
                splats: splat_count(&meta),
                collision: dir.join("collision.bin").is_file(),
                bytes: directory_size(&dir),
                converted: true,
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
            });
        }
    }

    found.sort_by(|a, b| a.name.to_ascii_lowercase().cmp(&b.name.to_ascii_lowercase()));
    found
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

pub fn bind(root: &Path, track: &str, scene: &str) -> io::Result<()> {
    let mut map = try_read_bindings(root)?;
    map.insert(track.to_string(), vec![scene.to_string()]);
    write_bindings(root, &map)
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
    }

    #[test]
    fn bindings_roundtrip_and_unbind() {
        let root = tmp();
        bind(&root, "VDGS X", "x-dir").unwrap();
        bind(&root, "VDGS Y", "y-dir").unwrap();
        let b = read_bindings(&root);
        assert_eq!(b["VDGS X"], vec!["x-dir"]);
        assert_eq!(b.len(), 2);
        assert!(unbind(&root, "VDGS X").unwrap());
        assert!(!unbind(&root, "VDGS X").unwrap());
        assert_eq!(read_bindings(&root).len(), 1);
    }

    #[test]
    fn bind_refuses_corrupt_bindings_and_leaves_file() {
        let root = tmp();
        let path = root.join("vdgs/bindings.json");
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        let corrupt = b"{\"Other\":[\"scene\"]\nnot-json";
        std::fs::write(&path, corrupt).unwrap();
        let before = std::fs::read(&path).unwrap();
        assert!(bind(&root, "VDGS X", "x-dir").is_err());
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
            bind(&root, "Existing", "cap-a").unwrap();
            let path = root.join("vdgs/bindings.json");
            std::fs::write(&path, bad).unwrap();
            assert!(
                bind(&root, "New", "cap-b").is_err(),
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
