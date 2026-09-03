//! Spawn the game and check if it is running.

use std::path::Path;
use std::process::{Command, Stdio};

use sysinfo::{ProcessRefreshKind, ProcessStatus, ProcessesToUpdate, System};

#[cfg(target_os = "macos")]
use std::collections::BTreeMap;

/// Doorstop + dyld env matching `run_bepinex.sh` defaults (absolute target assembly).
/// macOS only — Windows injects through `winhttp.dll` and needs no env.
#[cfg(target_os = "macos")]
pub fn doorstop_env(app: &Path) -> BTreeMap<String, String> {
    let root = app.parent().expect("game .app path has a parent");
    let mut e = BTreeMap::new();
    e.insert("DOORSTOP_ENABLED".into(), "1".into());
    e.insert(
        "DOORSTOP_TARGET_ASSEMBLY".into(),
        root.join("BepInEx/core/BepInEx.Preloader.dll")
            .to_string_lossy()
            .into_owned(),
    );
    e.insert("DOORSTOP_IGNORE_DISABLED_ENV".into(), "0".into());
    e.insert("DOORSTOP_MONO_DEBUG_ENABLED".into(), "0".into());
    e.insert("DOORSTOP_MONO_DEBUG_SUSPEND".into(), "0".into());
    e.insert(
        "DOORSTOP_MONO_DEBUG_ADDRESS".into(),
        "127.0.0.1:10000".into(),
    );
    e.insert("DOORSTOP_MONO_DLL_SEARCH_PATH_OVERRIDE".into(), String::new());
    e.insert("DOORSTOP_CLR_RUNTIME_CORECLR_PATH".into(), String::new());
    e.insert("DOORSTOP_CLR_CORLIB_DIR".into(), String::new());
    e.insert(
        "DYLD_LIBRARY_PATH".into(),
        root.to_string_lossy().into_owned(),
    );
    e.insert("DYLD_INSERT_LIBRARIES".into(), "libdoorstop.dylib".into());
    e
}

/// macOS: `arch -arm64` + Doorstop env. (Metal is the game's normal path; no D3D flag.)
#[cfg(target_os = "macos")]
pub fn spawn(app: &Path) -> std::io::Result<std::process::Child> {
    let root = app.parent().ok_or_else(|| {
        std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            "game .app path has no parent",
        )
    })?;
    let exe = app.join("Contents/MacOS/velocidrone");
    if !exe.is_file() {
        return Err(std::io::Error::new(
            std::io::ErrorKind::NotFound,
            format!("missing {}", exe.display()),
        ));
    }
    Command::new("/usr/bin/arch")
        .arg("-arm64")
        .arg("-e")
        .arg("DYLD_INSERT_LIBRARIES=libdoorstop.dylib")
        .arg(&exe)
        .envs(doorstop_env(app))
        .current_dir(root)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
}

/// Windows: `velocidrone.exe -force-d3d12`. Without that flag the splat shaders are
/// unsupported and nothing says why. `winhttp.dll` injects Doorstop on its own.
#[cfg(windows)]
pub fn spawn(game: &Path) -> std::io::Result<std::process::Child> {
    let exe = game.join("velocidrone.exe");
    if !exe.is_file() {
        return Err(std::io::Error::new(
            std::io::ErrorKind::NotFound,
            format!("missing {}", exe.display()),
        ));
    }
    Command::new(&exe)
        .arg("-force-d3d12")
        .current_dir(game)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
}

/// Whether a listed process counts as VelociDrone still running.
/// macOS: zombies are left behind when we spawn and never wait — they must not block installs.
#[cfg(target_os = "macos")]
pub(crate) fn is_live_game_process(name: &std::ffi::OsStr, status: ProcessStatus) -> bool {
    name == "velocidrone" && status != ProcessStatus::Zombie
}

/// Whether a listed process counts as VelociDrone still running.
/// Windows: name match only — zombie status is a Unix concept.
///
/// The trailing `.exe` is stripped case-insensitively rather than matched, so that neither
/// spelling nor casing of what sysinfo reports is something this depends on. The cost of
/// getting it wrong is not silent here the way it is on macOS — Windows holds a lock on a
/// running executable, so the install fails partway with a file-in-use error the person
/// cannot act on — but a half-replaced loader is still worth not creating. Stripping makes
/// every spelling mean what `GetProcessesByName("velocidrone")` means.
#[cfg(windows)]
pub(crate) fn is_live_game_process(name: &std::ffi::OsStr, _status: ProcessStatus) -> bool {
    let Some(name) = name.to_str() else {
        return false;
    };
    let stem = match name.len().checked_sub(4) {
        Some(cut) if name[cut..].eq_ignore_ascii_case(".exe") => &name[..cut],
        _ => name,
    };
    stem.eq_ignore_ascii_case("velocidrone")
}

pub fn is_running() -> bool {
    let mut sys = System::new();
    sys.refresh_processes_specifics(ProcessesToUpdate::All, true, ProcessRefreshKind::new());
    sys.processes()
        .values()
        .any(|p| is_live_game_process(p.name(), p.status()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::OsStr;
    use sysinfo::ProcessStatus;

    #[cfg(target_os = "macos")]
    #[test]
    fn env_for_doorstop() {
        use std::path::PathBuf;
        let app = PathBuf::from("/tmp/Data/velocidrone.app");
        let e = doorstop_env(&app);
        assert_eq!(e["DOORSTOP_ENABLED"], "1");
        assert_eq!(
            e["DOORSTOP_TARGET_ASSEMBLY"],
            "/tmp/Data/BepInEx/core/BepInEx.Preloader.dll"
        );
        assert_eq!(e["DYLD_LIBRARY_PATH"], "/tmp/Data");
        assert_eq!(e["DYLD_INSERT_LIBRARIES"], "libdoorstop.dylib");
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn zombie_velocidrone_is_not_running() {
        let name = OsStr::new("velocidrone");
        assert!(!is_live_game_process(name, ProcessStatus::Zombie));
        assert!(is_live_game_process(name, ProcessStatus::Run));
        assert!(is_live_game_process(name, ProcessStatus::Sleep));
        assert!(!is_live_game_process(OsStr::new("other"), ProcessStatus::Run));
    }

    #[cfg(windows)]
    #[test]
    fn windows_process_name_is_exe() {
        // Every spelling counts - which one sysinfo reports is not ours to depend on.
        for name in ["velocidrone.exe", "velocidrone", "VelociDrone.exe", "Velocidrone.Exe"] {
            assert!(is_live_game_process(OsStr::new(name), ProcessStatus::Run), "{name}");
        }
        assert!(!is_live_game_process(OsStr::new("other.exe"), ProcessStatus::Run));
        assert!(!is_live_game_process(OsStr::new("exe"), ProcessStatus::Run));
    }
}
