//! Persist companion settings under Application Support.

use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;

#[derive(Default, Serialize, Deserialize, Clone)]
pub struct Settings {
    pub game: Option<String>,
    pub catalog_url: Option<String>,
}

impl Settings {
    /// ~/Library/Application Support/VDGSCompanion/settings.json
    pub fn path() -> PathBuf {
        dirs::data_dir()
            .unwrap_or_else(|| PathBuf::from("."))
            .join("VDGSCompanion")
            .join("settings.json")
    }

    pub fn load() -> Settings {
        let path = Self::path();
        match fs::read_to_string(&path) {
            Ok(text) => serde_json::from_str(&text).unwrap_or_default(),
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
