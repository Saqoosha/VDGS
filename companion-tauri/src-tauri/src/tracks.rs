//! Read and write VelociDrone's track table (user11.db).

use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use percent_encoding::percent_decode_str;
use rusqlite::{Connection, OpenFlags};
use serde::Deserialize;
use thiserror::Error;

#[derive(Debug, Error)]
pub enum Error {
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Sqlite(#[from] rusqlite::Error),
}

#[derive(Clone, Debug)]
pub struct Track {
    pub id: i64,
    pub scene_id: i64,
    pub name: String,
    pub value: String,
    pub kind: i64,
    pub from_server: bool,
}

#[derive(Debug, PartialEq)]
pub enum ImportResult {
    Added,
    AlreadyPresent,
    WouldOverwrite,
}

#[derive(Deserialize)]
pub struct TrackFile {
    pub name: String,
    pub scene_id: i64,
    #[serde(default, rename = "type")]
    pub kind: i64,
    pub value: serde_json::Value,
}

impl TrackFile {
    /// String values as-is; object/array re-serialised compact.
    pub fn value_string(&self) -> String {
        match &self.value {
            serde_json::Value::String(s) => s.clone(),
            other => serde_json::to_string(other).unwrap_or_else(|_| "null".to_string()),
        }
    }
}

/// macOS: ~/Library/Application Support/com.velocidrone.velocidrone/user11.db
#[cfg(target_os = "macos")]
pub fn db_path() -> PathBuf {
    dirs::home_dir()
        .unwrap_or_default()
        .join("Library/Application Support/com.velocidrone.velocidrone/user11.db")
}

/// Windows: %USERPROFILE%\AppData\LocalLow\velocidrone\velocidrone\user11.db
#[cfg(windows)]
pub fn db_path() -> PathBuf {
    win_db_path(&dirs::home_dir().unwrap_or_default())
}

/// Windows user11.db path from a home directory (TrackStore.DatabasePath).
pub fn win_db_path(home: &Path) -> PathBuf {
    home.join("AppData")
        .join("LocalLow")
        .join("velocidrone")
        .join("velocidrone")
        .join("user11.db")
}

/// Form-decode: `+` → space, then `%XX`. Order matches TrackStore.DisplayName.
pub fn display_name(stored: &str) -> String {
    let spaced = stored.replace('+', " ");
    match percent_decode_str(&spaced).decode_utf8() {
        Ok(decoded) => decoded.into_owned(),
        Err(_) => spaced,
    }
}

pub fn list(db: &Path) -> rusqlite::Result<Vec<Track>> {
    let c = open_ro(db)?;
    let mut stmt = c.prepare(
        "select id, scene_id, name, value, type, online_id, protected_track from tracks",
    )?;
    let rows = stmt.query_map([], |r| {
        let value: Option<String> = r.get(3)?;
        let kind: Option<i64> = r.get(4)?;
        let online_id: Option<i64> = r.get(5)?;
        let protected_track: Option<i64> = r.get(6)?;
        Ok(Track {
            id: r.get(0)?,
            scene_id: r.get(1)?,
            name: r.get(2)?,
            value: value.unwrap_or_default(),
            kind: kind.unwrap_or(0),
            from_server: online_id.unwrap_or(0) != 0 || protected_track.unwrap_or(0) != 0,
        })
    })?;
    let mut found = Vec::new();
    for row in rows {
        found.push(row?);
    }
    Ok(found)
}

/// Whether VelociDrone's True Lens setting is on.
///
/// None means we do not know (db missing, unreadable, or no such row) — that must NOT be
/// shown as a warning. With it on the mod draws every capture and none of it reaches the
/// screen; every log says success and the sky is empty, so a note on the website is
/// useless. Related rows (true_lens_size, true_lens_quality) exist; match the exact name
/// only.
pub fn true_lens_on(db: &Path) -> Option<bool> {
    let c = open_ro(db).ok()?;
    let value: String = c
        .query_row(
            "select value from sim_states where name = ?1",
            ["true_lens"],
            |r| r.get(0),
        )
        .ok()?;
    match value.as_str() {
        "true" => Some(true),
        "false" => Some(false),
        _ => None,
    }
}

/// Exact stored name first, then display_name(row.name). Input is never decoded.
pub fn find(db: &Path, name: &str) -> rusqlite::Result<Option<Track>> {
    let all = list(db)?;
    for t in &all {
        if t.name == name {
            return Ok(Some(t.clone()));
        }
    }
    for t in &all {
        if display_name(&t.name) == name {
            return Ok(Some(t.clone()));
        }
    }
    Ok(None)
}

pub fn import(
    db: &Path,
    name: &str,
    scene_id: i64,
    kind: i64,
    value: &str,
) -> Result<(ImportResult, Option<PathBuf>), Error> {
    if let Some(existing) = find(db, name)? {
        let result = if existing.value == value {
            ImportResult::AlreadyPresent
        } else {
            ImportResult::WouldOverwrite
        };
        return Ok((result, None));
    }

    let backup_path = backup(db)?;
    let date = format_local_datetime();
    {
        let c = Connection::open(db)?;
        c.execute(
            "insert into tracks (scene_id, name, value, protected_track, online_id, rating, favourite, date, type) \
             values (?1, ?2, ?3, 0, 0, 0, 0, ?4, ?5)",
            rusqlite::params![scene_id, name, value, date, kind],
        )?;
    }
    Ok((ImportResult::Added, Some(backup_path)))
}

pub fn remove(db: &Path, name: &str) -> Result<(bool, Option<PathBuf>), Error> {
    let Some(t) = find(db, name)? else {
        return Ok((false, None));
    };
    if t.from_server {
        return Ok((false, None));
    }

    let backup_path = backup(db)?;
    let n = {
        let c = Connection::open(db)?;
        c.execute(
            "delete from tracks where id = ?1 and online_id = 0 and protected_track = 0",
            rusqlite::params![t.id],
        )?
    };
    Ok((n == 1, Some(backup_path)))
}

/// `<db>.vdgs-backup-YYYYmmdd-HHMMSS`
pub fn backup(db: &Path) -> std::io::Result<PathBuf> {
    let dest = PathBuf::from(format!(
        "{}.vdgs-backup-{}",
        db.display(),
        format_local_compact()
    ));
    // Match TrackStore.Remove: same-second path is left alone.
    if !dest.exists() {
        std::fs::copy(db, &dest)?;
    }
    Ok(dest)
}

fn open_ro(db: &Path) -> rusqlite::Result<Connection> {
    Connection::open_with_flags(db, OpenFlags::SQLITE_OPEN_READ_ONLY)
}

/// Local wall clock as `YYYY-MM-DD HH:MM:SS` (no chrono).
fn format_local_datetime() -> String {
    let (y, mo, d, h, mi, s) = local_ymdhms();
    format!("{y:04}-{mo:02}-{d:02} {h:02}:{mi:02}:{s:02}")
}

/// Local wall clock as `YYYYmmdd-HHMMSS` for backup filenames.
fn format_local_compact() -> String {
    let (y, mo, d, h, mi, s) = local_ymdhms();
    format!("{y:04}{mo:02}{d:02}-{h:02}{mi:02}{s:02}")
}

fn local_ymdhms() -> (i32, u32, u32, u32, u32, u32) {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0);
    local_ymdhms_at(secs)
}

#[cfg(unix)]
fn local_ymdhms_at(secs: i64) -> (i32, u32, u32, u32, u32, u32) {
    // macOS/Darwin `struct tm` layout; no chrono / libc crate.
    #[repr(C)]
    struct Tm {
        tm_sec: i32,
        tm_min: i32,
        tm_hour: i32,
        tm_mday: i32,
        tm_mon: i32,
        tm_year: i32,
        tm_wday: i32,
        tm_yday: i32,
        tm_isdst: i32,
        tm_gmtoff: i64,
        tm_zone: *const i8,
    }
    extern "C" {
        fn localtime_r(timep: *const i64, result: *mut Tm) -> *mut Tm;
    }

    unsafe {
        let mut tm = std::mem::MaybeUninit::<Tm>::zeroed();
        let ptr = localtime_r(&secs, tm.as_mut_ptr());
        if ptr.is_null() {
            return (1970, 1, 1, 0, 0, 0);
        }
        let tm = tm.assume_init();
        (
            tm.tm_year + 1900,
            (tm.tm_mon + 1) as u32,
            tm.tm_mday as u32,
            tm.tm_hour as u32,
            tm.tm_min as u32,
            tm.tm_sec as u32,
        )
    }
}

#[cfg(not(unix))]
fn local_ymdhms_at(secs: i64) -> (i32, u32, u32, u32, u32, u32) {
    // UTC fallback for non-unix targets (companion is macOS-only).
    let days = secs.div_euclid(86_400);
    let tod = secs.rem_euclid(86_400) as u32;
    let h = tod / 3600;
    let mi = (tod % 3600) / 60;
    let s = tod % 60;
    let (y, mo, d) = civil_from_days(days + 719_468);
    (y, mo, d, h, mi, s)
}

#[cfg(not(unix))]
fn civil_from_days(z: i64) -> (i32, u32, u32) {
    let era = if z >= 0 { z } else { z - 146_096 }.div_euclid(146_097);
    let doe = (z - era * 146_097) as u32;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = if m <= 2 { y + 1 } else { y };
    (y as i32, m, d)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn display_name_decodes_plus_then_percent() {
        assert_eq!(display_name("VDGS+FDF+2026-08-22"), "VDGS FDF 2026-08-22");
        assert_eq!(display_name("Sols%2bStreet%2bLeague%2b1"), "Sols+Street+League+1");
        assert_eq!(display_name("50%+off"), "50% off"); // stray % survives
    }

    #[test]
    fn win_db_path_is_locallow() {
        let home = Path::new("/Users/player");
        assert_eq!(
            win_db_path(home),
            home.join("AppData/LocalLow/velocidrone/velocidrone/user11.db")
        );
    }
    #[test]
    fn import_three_ways_and_remove_guard() {
        let db = fresh_db(); // creates the tracks table with the real schema (see below)
        let (r, b) = import(&db, "VDGS+X", 16, 0, "{\"gates\":[]}").unwrap();
        assert_eq!(r, ImportResult::Added);
        assert!(b.unwrap().exists());
        assert_eq!(
            import(&db, "VDGS+X", 16, 0, "{\"gates\":[]}").unwrap().0,
            ImportResult::AlreadyPresent
        );
        assert_eq!(
            import(&db, "VDGS+X", 16, 0, "{\"gates\":[1]}").unwrap().0,
            ImportResult::WouldOverwrite
        );
        assert_eq!(find(&db, "VDGS X").unwrap().unwrap().name, "VDGS+X"); // display form finds the row
                                                                          // a server row cannot be removed
        let c = rusqlite::Connection::open(&db).unwrap();
        c.execute(
            "insert into tracks (scene_id,name,value,protected_track,online_id) values (16,'Srv','{}',1,42)",
            [],
        )
        .unwrap();
        assert_eq!(remove(&db, "Srv").unwrap().0, false);
        assert_eq!(remove(&db, "VDGS+X").unwrap().0, true);
        assert!(find(&db, "VDGS+X").unwrap().is_none());
    }

    #[test]
    fn true_lens_on_reads_exact_row() {
        // Plain text in sim_states; related rows (true_lens_size, …) must not count.
        let db = fresh_sim_db();
        assert_eq!(true_lens_on(&db), None);
        let c = rusqlite::Connection::open(&db).unwrap();
        c.execute(
            "insert into sim_states (name, value) values ('true_lens_size', 'true')",
            [],
        )
        .unwrap();
        assert_eq!(true_lens_on(&db), None);
        c.execute(
            "insert into sim_states (name, value) values ('true_lens', 'true')",
            [],
        )
        .unwrap();
        assert_eq!(true_lens_on(&db), Some(true));
        c.execute(
            "update sim_states set value = 'false' where name = 'true_lens'",
            [],
        )
        .unwrap();
        assert_eq!(true_lens_on(&db), Some(false));
    }

    fn fresh_db() -> PathBuf {
        let p = std::env::temp_dir().join(format!("vdgs-tracks-{}.db", std::process::id()));
        let _ = std::fs::remove_file(&p);
        let c = rusqlite::Connection::open(&p).unwrap();
        c.execute_batch("CREATE TABLE [tracks] ([id] INTEGER NOT NULL PRIMARY KEY, [scene_id] INTEGER NOT NULL, [name] VARCHAR, [value] VARCHAR, [protected_track] TINYINT(1) NOT NULL DEFAULT 0, online_id int default 0, rating int default 0, favourite int default 0, date varchar default '2019-07-01 00:00:00', type int default 0);").unwrap();
        p
    }

    fn fresh_sim_db() -> PathBuf {
        let p = std::env::temp_dir().join(format!("vdgs-sim-{}.db", std::process::id()));
        let _ = std::fs::remove_file(&p);
        let c = rusqlite::Connection::open(&p).unwrap();
        c.execute_batch("CREATE TABLE [sim_states] ([name] VARCHAR, [value] VARCHAR);")
            .unwrap();
        p
    }
}
