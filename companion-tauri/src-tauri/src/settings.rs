//! Persist companion settings under the OS app-data folder.

use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;

/// The aliases read the C# companion's file, which System.Text.Json wrote with the
/// property names as spelled in the class - `Game`, `CatalogUrl`. Serde is case-sensitive,
/// so without them a machine upgrading from that app silently forgets where its game is,
/// which is the one thing this file exists to remember, and the person most affected is
/// the one who had to go and point at the folder by hand. Writing stays snake_case: the
/// files already on macOS are in that spelling.
#[derive(Default, Serialize, Deserialize, Clone)]
pub struct Settings {
    #[serde(alias = "Game")]
    pub game: Option<String>,
    #[serde(alias = "CatalogUrl")]
    pub catalog_url: Option<String>,
}

/// Drops a leading UTF-8 BOM, which `serde_json` treats as a parse error.
///
/// Every file the C# companion wrote has one: it saved with `Encoding.UTF8`, and that
/// overload emits the identifier - `new UTF8Encoding(false)` is the one that does not. So
/// without this, no machine upgrading from that app can read its own settings, and the
/// property aliases above never get the chance to matter. Notepad puts one there too, and
/// this file is meant to be hand-edited: `catalog_url` exists so someone hosting their own
/// list does not need a build. Failing to parse costs both settings and says nothing.
fn strip_bom(text: &str) -> &str {
    text.strip_prefix('\u{feff}').unwrap_or(text)
}

impl Settings {
    /// macOS: ~/Library/Application Support/VDGSCompanion/settings.json (`dirs::data_dir`).
    ///
    /// Windows: %LOCALAPPDATA%\VDGSCompanion\settings.json (`dirs::data_local_dir`).
    /// Do not use `data_dir()` on Windows — that is Roaming, and the C# companion writes
    /// under LocalApplicationData; using Roaming would orphan existing settings.
    pub fn path() -> PathBuf {
        #[cfg(target_os = "macos")]
        {
            dirs::data_dir()
                .unwrap_or_else(|| PathBuf::from("."))
                .join("VDGSCompanion")
                .join("settings.json")
        }
        #[cfg(windows)]
        {
            dirs::data_local_dir()
                .unwrap_or_else(|| PathBuf::from("."))
                .join("VDGSCompanion")
                .join("settings.json")
        }
    }

    pub fn load() -> Settings {
        let path = Self::path();
        match fs::read_to_string(&path) {
            Ok(text) => serde_json::from_str(strip_bom(&text)).unwrap_or_default(),
            Err(_) => Settings::default(),
        }
    }

    pub fn save(&self) {
        let path = Self::path();
        if let Some(parent) = path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if let Ok(text) = serde_json::to_string_pretty(self) {
            let _ = fs::write(path, text);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_the_csharp_companions_spelling_and_its_own() {
        let theirs: Settings =
            serde_json::from_str(r#"{"Game":"C:\\VelociDrone","CatalogUrl":null}"#).unwrap();
        assert_eq!(theirs.game.as_deref(), Some(r"C:\VelociDrone"));

        let ours: Settings =
            serde_json::from_str(r#"{"game":"/Data/velocidrone.app","catalog_url":null}"#).unwrap();
        assert_eq!(ours.game.as_deref(), Some("/Data/velocidrone.app"));

        let bommed = format!("\u{feff}{}", r#"{"Game":"C:\\VelociDrone"}"#);
        let from_csharp: Settings = serde_json::from_str(strip_bom(&bommed)).unwrap();
        assert_eq!(from_csharp.game.as_deref(), Some(r"C:\VelociDrone"));

        // What we write stays snake_case - the macOS files on disk are in that spelling.
        let text = serde_json::to_string(&ours).unwrap();
        assert!(text.contains("\"game\""), "{text}");
        assert!(text.contains("\"catalog_url\""), "{text}");
    }
}
