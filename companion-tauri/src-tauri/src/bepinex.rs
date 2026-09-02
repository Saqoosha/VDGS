//! Install and uninstall the patched BepInEx loader.

use std::path::Path;
use std::process::Command;

use crate::catalog;

pub const VERSION: &str = "5.4.23.5-vdgs.1";

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

/// Records which BepInEx this app put there.
///
/// The loader's own files do not say. An official macOS BepInEx has the same dylib and the
/// same preloader path as the patched one, so "is BepInEx here" cannot answer "is the
/// preloader the one that survives arm64" - and the official one does not: it dies before
/// the chainloader and the game starts with no plugins. Skipping the install on the
/// strength of those files would drop our plugin into a loader that never runs it, and
/// then report the machine ready.
fn stamp_path(root: &Path) -> std::path::PathBuf {
    root.join("BepInEx/vdgs-bepinex-version.txt")
}

/// Whether the loader here is the build this app installs.
///
/// Anything else - the official release, an older fork, a hand-unzipped copy - reads as
/// false, so it is replaced. Replacing is safe: the extract keeps an existing BepInEx.cfg.
pub fn is_ours(root: &Path) -> bool {
    std::fs::read_to_string(stamp_path(root))
        .map(|s| s.trim() == VERSION)
        .unwrap_or(false)
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
        catalog::extract(&zip, root, &["BepInEx.cfg"], log)?;
        strip_quarantine(&root.join("libdoorstop.dylib"));
        strip_quarantine(&root.join("BepInEx"));
        write_logging_config(root)?;
        std::fs::write(stamp_path(root), VERSION)?;
        log(format!("installed BepInEx {VERSION}"));
        Ok(())
    })();
    let _ = std::fs::remove_file(&zip);
    result
}

pub fn uninstall(root: &Path, log: &mut dyn FnMut(String)) -> std::io::Result<()> {
    for name in [
        "BepInEx",
        "libdoorstop.dylib",
        "run_bepinex.sh",
        "README-vdgs.txt",
        "changelog.txt",
    ] {
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
    }
}
