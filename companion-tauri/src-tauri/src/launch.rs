//! Spawn the game through Doorstop and check if it is running.

use std::collections::BTreeMap;
use std::path::Path;
use std::process::{Command, Stdio};

use sysinfo::{ProcessRefreshKind, ProcessesToUpdate, System};

/// Doorstop + dyld env matching `run_bepinex.sh` defaults (absolute target assembly).
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

pub fn is_running() -> bool {
    let mut sys = System::new();
    sys.refresh_processes_specifics(ProcessesToUpdate::All, true, ProcessRefreshKind::new());
    sys.processes()
        .values()
        .any(|p| p.name() == "velocidrone")
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn env_for_doorstop() {
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
}
