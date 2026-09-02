//! Catalog fetch, download with digest, and zip extract.

use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub const DEFAULT_URL: &str = "https://vdgs.saqoo.sh/catalog.json";

#[derive(Clone, Debug, Deserialize)]
pub struct FileRef {
    pub url: String,
    #[serde(default)]
    pub bytes: u64,
    #[serde(default)]
    pub sha256: Option<String>,
}

#[derive(Clone, Debug)]
pub struct Entry {
    pub id: String,
    pub name: String,
    pub description: Option<String>,
    pub author: Option<String>,
    pub licence: Option<String>,
    pub splats: u64,
    pub scene: FileRef,
    pub install_as: Option<String>,
    pub track: Option<FileRef>,
    pub track_name: Option<String>,
}

impl Entry {
    pub fn bytes(&self) -> u64 {
        self.scene.bytes + self.track.as_ref().map(|t| t.bytes).unwrap_or(0)
    }
}

#[derive(thiserror::Error, Debug)]
pub enum Error {
    #[error("{0}")]
    Msg(String),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Http(#[from] reqwest::Error),
    #[error(transparent)]
    Zip(#[from] zip::result::ZipError),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
}

pub fn require_safe_url(url: &str) -> Result<(), Error> {
    let uri = reqwest::Url::parse(url).map_err(|_| Error::Msg(format!("not a URL: {url}")))?;
    match uri.scheme() {
        "https" => Ok(()),
        "http" => {
            let host = uri.host_str().unwrap_or("");
            if host == "localhost" || host == "127.0.0.1" || host == "::1" {
                Ok(())
            } else {
                Err(Error::Msg(format!("refusing a non-https address: {url}")))
            }
        }
        _ => Err(Error::Msg(format!("refusing a non-https address: {url}"))),
    }
}

pub fn parse(json: &str) -> Result<Vec<Entry>, Error> {
    let root: serde_json::Value = serde_json::from_str(json)?;

    if let Some(v) = root.get("formatVersion") {
        if let Some(n) = v.as_i64() {
            if n != 1 {
                return Err(Error::Msg(format!(
                    "this catalog is format {n}; this app reads 1. Update it."
                )));
            }
        }
    }

    let scenes = root
        .get("scenes")
        .and_then(|s| s.as_array())
        .ok_or_else(|| Error::Msg("no scenes in the catalog".into()))?;

    let mut found = Vec::new();
    for e in scenes {
        let id = str_field(e, "id");
        let name = str_field(e, "name");
        let description = str_field(e, "description");
        let author = str_field(e, "author");
        let licence = str_field(e, "licence");
        let splats = num_field(e, "splats");

        let (scene, install_as) = match e.get("scene").filter(|s| s.is_object()) {
            Some(scene) => (Some(read_file(scene)), str_field(scene, "installAs")),
            None => (None, None),
        };

        let (track, track_name) = match e.get("track").filter(|s| s.is_object()) {
            Some(track) => (Some(read_file(track)), str_field(track, "name")),
            None => (None, None),
        };

        let (Some(id), Some(name), Some(scene)) = (id, name, scene) else {
            continue;
        };

        found.push(Entry {
            id,
            name,
            description,
            author,
            licence,
            splats,
            scene,
            install_as,
            track,
            track_name,
        });
    }
    Ok(found)
}

fn str_field(e: &serde_json::Value, key: &str) -> Option<String> {
    e.get(key)
        .and_then(|v| v.as_str())
        .map(|s| s.to_string())
}

fn num_field(e: &serde_json::Value, key: &str) -> u64 {
    e.get(key).and_then(|v| v.as_u64()).unwrap_or(0)
}

fn read_file(e: &serde_json::Value) -> FileRef {
    FileRef {
        url: str_field(e, "url").unwrap_or_default(),
        bytes: num_field(e, "bytes"),
        sha256: str_field(e, "sha256"),
    }
}

pub fn fetch(url: &str) -> Result<Vec<Entry>, Error> {
    require_safe_url(url)?;
    let client = reqwest::blocking::Client::builder()
        .user_agent("VDGSCompanion")
        .timeout(Duration::from_secs(30))
        .build()?;
    let text = client.get(url).send()?.error_for_status()?.text()?;
    parse(&text)
}

pub fn download(
    file: &FileRef,
    into_dir: &Path,
    percent: &mut dyn FnMut(u8),
) -> Result<PathBuf, Error> {
    require_safe_url(&file.url)?;
    let expected = match file.sha256.as_deref() {
        Some(s) if !s.is_empty() => s,
        _ => {
            return Err(Error::Msg(format!(
                "the catalog gives no digest for {}",
                file.url
            )))
        }
    };

    std::fs::create_dir_all(into_dir)?;
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let temp = into_dir.join(format!("vdgs-{}-{}.part", nanos, std::process::id()));

    let result = (|| -> Result<PathBuf, Error> {
        // No deadline on the whole request. A capture is hundreds of megabytes and
        // reqwest's `timeout` covers reading the body too, so any total figure is a size
        // limit wearing a clock's clothes - the 30 s this started with would have failed
        // every real download. Connecting still has one, which is the half that hangs.
        let client = reqwest::blocking::Client::builder()
            .user_agent("VDGSCompanion")
            .timeout(None)
            .connect_timeout(Duration::from_secs(30))
            .build()?;
        let mut response = client.get(&file.url).send()?.error_for_status()?;
        let total = response.content_length().unwrap_or(file.bytes);

        let mut sink = std::fs::File::create(&temp)?;
        let mut buf = [0u8; 81920];
        let mut done: u64 = 0;
        let mut last_reported: i32 = -1;
        loop {
            let n = response.read(&mut buf)?;
            if n == 0 {
                break;
            }
            sink.write_all(&buf[..n])?;
            done += n as u64;
            if total == 0 {
                continue;
            }
            let p = (done * 100 / total) as u8;
            if p as i32 == last_reported {
                continue;
            }
            last_reported = p as i32;
            percent(p);
        }
        drop(sink);

        let actual = sha256_file(&temp)?;
        if !actual.eq_ignore_ascii_case(expected) {
            return Err(Error::Msg(
                "the download does not match the catalog's digest - it was truncated or is not the file that was published"
                    .into(),
            ));
        }
        Ok(temp.clone())
    })();

    if result.is_err() {
        let _ = std::fs::remove_file(&temp);
    }
    result
}

pub fn sha256_file(path: &Path) -> std::io::Result<String> {
    let mut file = std::fs::File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buf = [0u8; 81920];
    loop {
        let n = file.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(format!("{:x}", hasher.finalize()))
}

/// Extracts `zip` under `root`. Returns the files written. `keep_existing` names leaf files left alone when present (placement.json, bindings.json).
pub fn extract(
    zip: &Path,
    root: &Path,
    keep_existing: &[&str],
    log: &mut dyn FnMut(String),
) -> Result<Vec<PathBuf>, Error> {
    let file = std::fs::File::open(zip)?;
    let mut archive = zip::ZipArchive::new(file)?;
    let root = root.canonicalize()?;
    let mut written = Vec::new();

    for i in 0..archive.len() {
        let mut entry = archive.by_index(i)?;
        if entry.is_dir() {
            continue;
        }

        let enclosed = entry.enclosed_name().ok_or_else(|| {
            Error::Msg(format!("archive escapes the game folder: {}", entry.name()))
        })?;
        let name_str = enclosed.to_string_lossy();
        // A README at the top of the archive is for the person, not the game. It is the
        // note that is skipped, though, not everything at that level: BepInEx puts
        // libdoorstop.dylib beside its README, and dropping that leaves a loader the game
        // never loads - installed, reported installed, and inert.
        let top_level = !name_str.contains('/') && !name_str.contains('\\');
        if top_level && is_note(&name_str) {
            continue;
        }

        let target = root.join(enclosed);
        if !target.starts_with(&root) {
            return Err(Error::Msg(format!(
                "archive escapes the game folder: {}",
                entry.name()
            )));
        }

        if let Some(parent) = target.parent() {
            std::fs::create_dir_all(parent)?;
        }

        let leaf = target
            .file_name()
            .and_then(|s| s.to_str())
            .unwrap_or("");
        if keep_existing.iter().any(|k| *k == leaf) && target.exists() {
            log(format!("kept your {leaf}"));
            continue;
        }

        let mut out = std::fs::File::create(&target)?;
        std::io::copy(&mut entry, &mut out)?;
        written.push(target);
    }
    Ok(written)
}

/// A file at the top of an archive that is meant to be read, not installed.
fn is_note(name: &str) -> bool {
    let lower = name.to_ascii_lowercase();
    lower.ends_with(".txt") || lower.ends_with(".md")
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    #[test]
    fn parse_reads_scene_and_track() {
        let json = r#"{"formatVersion":1,"scenes":[{"id":"a","name":"A","splats":5,
      "scene":{"url":"https://x/s.zip","bytes":10,"sha256":"ab","installAs":"A-dir"},
      "track":{"url":"https://x/t.json","bytes":1,"sha256":"cd","name":"VDGS+A"}},
      {"id":"b","name":"B"}]}"#;
        let e = parse(json).unwrap();
        assert_eq!(e.len(), 1);
        assert_eq!(e[0].install_as.as_deref(), Some("A-dir"));
        assert_eq!(e[0].track_name.as_deref(), Some("VDGS+A"));
        assert_eq!(e[0].bytes(), 11);
    }

    #[test]
    fn parse_refuses_other_format() {
        assert!(parse(r#"{"formatVersion":2,"scenes":[]}"#).is_err());
    }

    #[test]
    fn safe_url() {
        assert!(require_safe_url("https://vdgs.saqoo.sh/x").is_ok());
        assert!(require_safe_url("http://127.0.0.1:8000/x").is_ok());
        assert!(require_safe_url("http://vdgs.saqoo.sh/x").is_err());
        assert!(require_safe_url("not a url").is_err());
    }

    #[test]
    fn extract_refuses_escape_and_keeps_placement() {
        let dir = tempdir();
        let zip_path = dir.join("t.zip");
        {
            let f = std::fs::File::create(&zip_path).unwrap();
            let mut w = zip::ZipWriter::new(f);
            let o = zip::write::SimpleFileOptions::default();
            w.start_file("vdgs/x/meta.json", o).unwrap();
            w.write_all(b"{}").unwrap();
            w.start_file("vdgs/x/placement.json", o).unwrap();
            w.write_all(b"new").unwrap();
            w.start_file("README.txt", o).unwrap();
            w.write_all(b"top").unwrap();
            w.start_file("libdoorstop.dylib", o).unwrap();
            w.write_all(b"loader").unwrap();
            w.finish().unwrap();
        }
        let root = dir.join("root");
        std::fs::create_dir_all(root.join("vdgs/x")).unwrap();
        std::fs::write(root.join("vdgs/x/placement.json"), b"mine").unwrap();
        let mut log = |_s: String| {};
        let written = extract(
            &zip_path,
            &root,
            &["placement.json", "bindings.json"],
            &mut log,
        )
        .unwrap();
        assert_eq!(
            std::fs::read(root.join("vdgs/x/placement.json")).unwrap(),
            b"mine"
        );
        assert!(root.join("vdgs/x/meta.json").exists());
        assert!(!root.join("README.txt").exists());
        // A note at the top is skipped; a loader at the top is not.
        assert_eq!(std::fs::read(root.join("libdoorstop.dylib")).unwrap(), b"loader");
        assert_eq!(written.len(), 2);
        // escaping entry
        let bad = dir.join("bad.zip");
        {
            let f = std::fs::File::create(&bad).unwrap();
            let mut w = zip::ZipWriter::new(f);
            w.start_file("../evil.txt", zip::write::SimpleFileOptions::default())
                .unwrap();
            w.write_all(b"x").unwrap();
            w.finish().unwrap();
        }
        assert!(extract(&bad, &root, &[], &mut log).is_err());
        assert!(!dir.join("evil.txt").exists());
    }

    fn tempdir() -> PathBuf {
        let p = std::env::temp_dir().join(format!("vdgs-test-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p).unwrap();
        p
    }
}
