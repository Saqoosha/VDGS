//! Install and uninstall the BepInEx loader.

use std::path::Path;

use crate::catalog;

#[cfg(target_os = "macos")]
use std::process::Command;

/// macOS: the patched universal build, with the arm64 MonoMod fix.
#[cfg(target_os = "macos")]
pub const VERSION: &str = "5.4.23.5-vdgs.1";

/// Windows: the official release, which needs no patch.
///
/// A log string only. `is_ours` does not compare against it, matching `MainForm.cs`, so an
/// older BepInEx already on the machine is kept rather than upgraded — do not read this
/// constant as a floor.
#[cfg(windows)]
pub const VERSION: &str = "5.4.23.5";

#[cfg(target_os = "macos")]
pub fn release() -> catalog::FileRef {
    catalog::FileRef {
        url: "https://github.com/Saqoosha/BepInEx/releases/download/v5.4.23.5-vdgs.1/BepInEx_macos_universal_5.4.23.5-vdgs.1.zip"
            .into(),
        bytes: 660321,
        sha256: Some(
            "950d55271c176c732fc896bcdae2750978ef92b940c951aa7fad0eb4251f1d61".into(),
        ),
    }
}

#[cfg(windows)]
pub fn release() -> catalog::FileRef {
    catalog::FileRef {
        url: "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip"
            .into(),
        bytes: 639118,
        sha256: Some(
            "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4".into(),
        ),
    }
}

/// Records which BepInEx this app put there.
///
/// macOS only. The loader's own files do not say. An official macOS BepInEx has the same
/// dylib and the same preloader path as the patched one, so "is BepInEx here" cannot answer
/// "is the preloader the one that survives arm64" - and the official one does not: it dies
/// before the chainloader and the game starts with no plugins. Skipping the install on the
/// strength of those files would drop our plugin into a loader that never runs it, and
/// then report the machine ready. Windows uses the official release, so there is nothing to
/// distinguish and no stamp is written.
#[cfg(target_os = "macos")]
fn stamp_path(root: &Path) -> std::path::PathBuf {
    root.join("BepInEx/vdgs-bepinex-version.txt")
}

/// Whether a loader this app can work with is already here.
///
/// macOS: both halves are needed. Without the stamp, another BepInEx - the official
/// release, an older fork, a hand-unzipped copy - passes as ours and is never replaced.
/// Without the file check, a stamp that outlived the files it describes says the loader is
/// fine while the game has nothing to load, and the install skips it, so the one button
/// that could repair the machine is the one that reports there is nothing to repair.
///
/// Windows: `has_bepinex` alone. The official win_x64 zip is the correct loader, so a stamp
/// that separates official from patched is not needed — but note what that leaves out: any
/// BepInEx passes, including an older one, matching `GameInstall.HasBepInEx`. The file half
/// of the invariant is kept, in `has_bepinex` itself.
#[cfg(target_os = "macos")]
pub fn is_ours(root: &Path) -> bool {
    crate::game::has_bepinex(root)
        && std::fs::read_to_string(stamp_path(root))
            .map(|s| s.trim() == VERSION)
            .unwrap_or(false)
}

#[cfg(windows)]
pub fn is_ours(root: &Path) -> bool {
    crate::game::has_bepinex(root)
}

pub fn install(
    root: &Path,
    log: &mut dyn FnMut(String),
    percent: &mut dyn FnMut(u8),
) -> Result<(), catalog::Error> {
    log(format!("fetching BepInEx {VERSION}"));
    let into = std::env::temp_dir().join("vdgs-download");
    let zip = catalog::download(&release(), &into, percent)?;
    let result = (|| {
        // Checked again here, after the download, for the reason the same check exists in
        // game.rs: the caller's check happened before a fetch, and replacing a loader the
        // running game has already mapped does not fail on macOS - it succeeds, and leaves
        // what is on disk disagreeing with what is loaded.
        if crate::launch::is_running() {
            return Err(catalog::Error::Msg(
                "VelociDrone is running. Close it first - files in use cannot be replaced."
                    .into(),
            ));
        }
        catalog::extract(&zip, root, &["BepInEx.cfg"], log)?;
        #[cfg(target_os = "macos")]
        {
            strip_quarantine(&root.join("libdoorstop.dylib"));
            strip_quarantine(&root.join("BepInEx"));
        }
        write_logging_config(root)?;
        #[cfg(target_os = "macos")]
        std::fs::write(stamp_path(root), VERSION)?;
        log(format!("installed BepInEx {VERSION}"));
        Ok(())
    })();
    let _ = std::fs::remove_file(&zip);
    result
}

pub fn uninstall(root: &Path, log: &mut dyn FnMut(String)) -> std::io::Result<()> {
    #[cfg(target_os = "macos")]
    let names = [
        "BepInEx",
        "libdoorstop.dylib",
        "run_bepinex.sh",
        "README-vdgs.txt",
        "changelog.txt",
    ];
    // The top-level files the pinned win_x64 zip actually lays down. `changelog.txt` is not
    // among them: `catalog::is_note` drops top-level `.txt` at install, so naming it here
    // removed nothing, while `.doorstop_version` was installed and left behind.
    #[cfg(windows)]
    let names = ["BepInEx", "winhttp.dll", "doorstop_config.ini", ".doorstop_version"];
    for name in names {
        let path = root.join(name);
        if path.is_dir() {
            std::fs::remove_dir_all(&path)?;
            log(format!("removed {name}"));
        } else if path.is_file() {
            std::fs::remove_file(&path)?;
            log(format!("removed {name}"));
        }
    }
    Ok(())
}

/// Turns on the disk log, once, before the game has ever written a config of its own.
///
/// Shorter than the Windows companion's version on purpose. That one also sets
/// `UnityLogListening = false`, because under `-force-d3d12` the game's own Auto Exposure
/// throws every frame and the listener copies the exception into BepInEx's log until it
/// reaches tens of megabytes. macOS renders through Metal and never passes that flag, so
/// there is no spam to suppress - and leaving the listener on is what puts the game's own
/// messages in the log, which is the first thing anyone reads when a capture does not
/// appear. AGENTS.md describes the Windows behaviour; this is the deliberate difference.
#[cfg(target_os = "macos")]
pub fn write_logging_config(root: &Path) -> std::io::Result<()> {
    let path = root.join("BepInEx/config/BepInEx.cfg");
    if path.exists() {
        return Ok(());
    }
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(
        path,
        "[Logging.Disk]\n\n## Enables writing log messages to disk.\n# Setting type: Boolean\n# Default value: false\nEnabled = true\n",
    )
}

/// BepInEx writes no config until the game has been run once, and its defaults are wrong
/// for this game in a way that costs real disk.
///
/// Under `-force-d3d12` the game's own Auto Exposure throws every frame - a fault in the
/// game, not the mod, and harmless to the picture. With Unity log listening on, that
/// exception is copied into the BepInEx log until it reaches tens of megabytes; one session
/// was measured at 64 MB. Turning listening off leaves the exceptions in Player.log where
/// they belong. (Matches BepInEx.cs::WriteLoggingConfig.)
#[cfg(windows)]
pub fn write_logging_config(root: &Path) -> std::io::Result<()> {
    let path = root.join("BepInEx/config/BepInEx.cfg");
    if path.exists() {
        return Ok(());
    }
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(
        path,
        "[Logging]\r\n\
         ## Whether to write the game's own Unity log into BepInEx's.\r\n\
         UnityLogListening = false\r\n\
         \r\n\
         [Logging.Disk]\r\n\
         Enabled = true\r\n\
         LogLevel = Fatal, Error, Warning, Message, Info\r\n",
    )
}

/// macOS only — `xattr` is not a Windows tool.
#[cfg(target_os = "macos")]
pub fn strip_quarantine(path: &Path) {
    let mut cmd = Command::new("xattr");
    if path.is_dir() {
        cmd.arg("-dr");
    } else {
        cmd.arg("-d");
    }
    let _ = cmd.arg("com.apple.quarantine").arg(path).status();
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn config_written_once() {
        let root = tmp();
        write_logging_config(&root).unwrap();
        let p = root.join("BepInEx/config/BepInEx.cfg");
        let first = std::fs::read_to_string(&p).unwrap();
        assert!(first.contains("[Logging.Disk]") && first.contains("Enabled = true"));
        std::fs::write(&p, "theirs").unwrap();
        write_logging_config(&root).unwrap();
        assert_eq!(std::fs::read_to_string(&p).unwrap(), "theirs");
    }

    fn tmp() -> PathBuf {
        static N: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
        let p = std::env::temp_dir().join(format!(
            "vdgs-bepinex-{}-{}",
            std::process::id(),
            N.fetch_add(1, std::sync::atomic::Ordering::SeqCst)
        ));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p).unwrap();
        p
    }

    /// macOS: stamp + files. Official-looking files alone must not count as ours.
    #[cfg(target_os = "macos")]
    #[test]
    fn only_our_own_build_counts_as_installed() {
        let root = tmp();
        // A loader that is present but not ours - the official macOS release looks exactly
        // like this - must not satisfy the check.
        std::fs::create_dir_all(root.join("BepInEx/core")).unwrap();
        std::fs::write(root.join("BepInEx/core/BepInEx.Preloader.dll"), b"x").unwrap();
        std::fs::write(root.join("libdoorstop.dylib"), b"x").unwrap();
        assert!(!is_ours(&root));

        std::fs::write(stamp_path(&root), VERSION).unwrap();
        assert!(is_ours(&root));

        std::fs::write(stamp_path(&root), "5.4.23.5-vdgs.0").unwrap();
        assert!(!is_ours(&root), "an older fork is replaced, not kept");

        // A stamp that outlived the loader must not report the loader as present, or the
        // install skips the very files that are missing and the app stays broken.
        std::fs::write(stamp_path(&root), VERSION).unwrap();
        std::fs::remove_file(root.join("libdoorstop.dylib")).unwrap();
        assert!(!is_ours(&root), "the stamp alone is not the loader");
    }

    #[cfg(windows)]
    #[test]
    fn windows_ours_is_has_bepinex_alone() {
        let root = tmp();
        assert!(!is_ours(&root));
        // A folder alone is not a loader: this is the shape an antivirus quarantine of
        // BepInEx/core leaves behind, and it must not report as installed.
        std::fs::create_dir_all(root.join("BepInEx/core")).unwrap();
        std::fs::write(root.join("winhttp.dll"), b"x").unwrap();
        assert!(!is_ours(&root), "an emptied BepInEx is not a loader");

        std::fs::write(root.join("BepInEx/core/BepInEx.Preloader.dll"), b"x").unwrap();
        assert!(is_ours(&root));

        std::fs::remove_file(root.join("BepInEx/core/BepInEx.Preloader.dll")).unwrap();
        assert!(!is_ours(&root), "a quarantined preloader is repairable, not ready");
    }

    /// The URL, byte count and sha256 in `release()` can rot without anybody touching this
    /// code — GitHub re-tagging a release, or an asset being replaced — and the first person
    /// to find out would otherwise be a user whose INSTALL BEPINEX button fails. It is
    /// `#[ignore]` because it needs the network, which mirrors how the deleted C# kept it in
    /// a separate opt-in test project rather than the default run.
    #[test]
    #[ignore = "reaches the network; run with --ignored at release time"]
    fn the_pinned_release_is_still_there() {
        let dir = tmp();
        let path = crate::catalog::download(&release(), &dir, &mut |_| {}).expect("pin still fetches");
        let _ = std::fs::remove_file(path);
    }
}
