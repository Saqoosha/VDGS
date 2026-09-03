//! The two things this app can do without opening a window.
//!
//! Both are here rather than in a separate tool because both need something this app
//! already carries. Publishing a track means getting it out of `user11.db`, and this is
//! the only thing on the machine that can read that file - a standalone exporter would be
//! a second copy of the schema to keep in step. Checking a catalog means answering "can
//! the app read this", which only the app's own parser can say.
//!
//! Ported from `companion/Program.cs`. That version was Windows-only because the C# app
//! was; this one runs on both, which is new rather than a port decision.

use std::path::{Path, PathBuf};

use crate::{catalog, tracks};

/// Runs a command if the arguments name one, and reports the exit code.
///
/// `None` means "no command here, open the window" - which is also what a bare `--` or an
/// unknown flag gets, matching Program.cs: an argument this does not recognise belongs to
/// Tauri or to the webview, and swallowing it would break the GUI to serve the CLI.
pub fn try_run(args: &[String]) -> Option<i32> {
    match args.first().map(String::as_str) {
        Some("--export-track") => Some(export_track(&args[1..])),
        Some("--check-catalog") => Some(check_catalog(args.get(1).map(String::as_str))),
        _ => None,
    }
}

fn check_catalog(url: Option<&str>) -> i32 {
    attach_console();
    let url = url.unwrap_or(catalog::DEFAULT_URL);
    match catalog::fetch(url) {
        Ok(entries) => {
            // Every field the C# printed. Dropping to bare ids was a real loss: this is
            // the one pass anyone makes before publishing, and `no licence` is the line it
            // exists to surface - an absent licence is not permission. `install_as` is the
            // directory a capture unpacks into, which is half of "the mod is installed and
            // nothing appears", and the track line says whether an entry carries a course
            // at all. A catalog that parses can still be all three of those things wrong.
            println!("{}: {} capture(s)", url, entries.len());
            for e in &entries {
                println!(
                    "  {}  {}  {} splats  {} MB  {}",
                    e.id,
                    e.name,
                    thousands(e.splats),
                    e.scene.bytes / 1_048_576,
                    e.licence.as_deref().unwrap_or("no licence"),
                );
                println!(
                    "      scene -> {}  {}",
                    e.install_as.as_deref().unwrap_or(&e.id),
                    e.scene.url
                );
                match (&e.track, &e.track_name) {
                    (Some(t), name) => println!(
                        "      track -> {}  {}",
                        name.as_deref().unwrap_or("(unnamed)"),
                        t.url
                    ),
                    (None, _) => println!("      track -> none"),
                }
            }
            0
        }
        Err(e) => {
            eprintln!("cannot read {url}: {e}");
            1
        }
    }
}

/// `1234567` as `1,234,567`, the way the C# formatted it with "N0".
fn thousands(n: u64) -> String {
    let digits = n.to_string();
    let mut out = String::with_capacity(digits.len() + digits.len() / 3);
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i) % 3 == 0 {
            out.push(',');
        }
        out.push(c);
    }
    out
}

fn export_track(rest: &[String]) -> i32 {
    attach_console();

    let Some(first) = rest.first().map(String::as_str) else {
        eprintln!("usage: VDGS --export-track \"<track name>\" [out.track.json]");
        eprintln!("       VDGS --export-track --list");
        return 2;
    };

    let db = tracks::db_path();
    if !db.is_file() {
        eprintln!("no track database at {}", db.display());
        return 1;
    }

    if first == "--list" {
        return list_tracks(&db);
    }

    let track = match tracks::find(&db, first) {
        Ok(Some(t)) => t,
        Ok(None) => {
            eprintln!("no track called \"{first}\" - try --list");
            return 1;
        }
        Err(e) => {
            eprintln!("cannot read {}: {e}", db.display());
            return 1;
        }
    };

    // Its author put it on the official server; republishing it here would be taking
    // someone else's course and handing it out under our own catalog.
    if track.from_server {
        eprintln!(
            "\"{}\" came from the official track server. Only tracks built locally are ours to publish.",
            track.name
        );
        return 1;
    }

    let out: PathBuf = match rest.get(1) {
        Some(p) => PathBuf::from(p),
        None => PathBuf::from(format!("{}.track.json", safe_file_name(&track.name))),
    };

    // `value` goes back out as the string the game stored, not re-encoded. Reformatting it
    // would make an imported track differ from the original for no reason anyone could see.
    let doc = serde_json::json!({
        "name": track.name,
        "scene_id": track.scene_id,
        "type": track.kind,
        "value": track.value,
    });
    let text = match serde_json::to_string_pretty(&doc) {
        Ok(t) => t,
        Err(e) => {
            eprintln!("cannot build the file: {e}");
            return 1;
        }
    };
    let bytes = text.len();
    if let Err(e) = std::fs::write(&out, text.as_bytes()) {
        eprintln!("cannot write {}: {e}", out.display());
        return 1;
    }
    // The length we just wrote, not a fresh stat. `unwrap_or(0)` on a stat was the first
    // version, and zero bytes is exactly what a failed export looks like - the fallback
    // manufactured the most misleading line available.
    println!("wrote {} ({bytes} bytes)", out.display());
    0
}

fn list_tracks(db: &Path) -> i32 {
    let all = match tracks::list(db) {
        Ok(a) => a,
        Err(e) => {
            eprintln!("cannot read {}: {e}", db.display());
            return 1;
        }
    };
    for t in &all {
        // scene_id is the scenery the course sits on, and a capture is placed relative to
        // that scenery's origin - so it is the number that decides whether a published
        // track lands where its capture is.
        // Spacing matches Program.cs, because the sample output in TRACKS.md was copied
        // from it and would otherwise be one column out.
        let where_from = if t.from_server { "[server] " } else { "[local]  " };
        println!("{where_from}scene {:>3}  {}", t.scene_id, t.name);
    }
    0
}

/// A name that survives being a filename, matching Program.cs's SafeFileName.
///
/// Only the characters a filename genuinely cannot hold are replaced, and everything else
/// — spaces, `+`, every non-ASCII character — is kept exactly as the database spelled it.
/// The first version of this was stricter, folding anything outside `[A-Za-z0-9_-]` into a
/// dash, collapsing runs and falling back to the word "track". That reads as tidier and is
/// worse: `VDGS FDF`, `VDGS+FDF` and `VDGS-FDF` are three different rows in `user11.db`
/// and all three collapsed onto one filename, while every all-Japanese name collapsed onto
/// `track`. `fs::write` truncates, so exporting two of them in a row destroyed the first
/// and printed `wrote ... (N bytes)` both times.
fn safe_file_name(name: &str) -> String {
    // The Windows set, which is the wider of the two - the C# this ports ran only there,
    // and a name that is safe on Windows is safe on macOS.
    const INVALID: &[char] = &['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    name.chars()
        .map(|c| if INVALID.contains(&c) || (c as u32) < 32 { '-' } else { c })
        .collect()
}

/// Borrow the console of whoever launched this, on Windows.
///
/// The GUI build sets `windows_subsystem = "windows"` so that double-clicking it does not
/// flash a console. The cost is that a process started that way has no console at all, and
/// every `println!` goes to a handle that is not connected to anything - the command
/// appears to do nothing and return instantly. `AttachConsole(ATTACH_PARENT_PROCESS)` joins
/// the caller's, and the std handles are then pointed at it, because attaching alone does
/// not repoint them.
///
/// The check for an existing handle is not a shortcut. Redirected output - a pipe, a file,
/// anything a script does - arrives as a perfectly good handle, and repointing that at the
/// console throws the redirection away: the caller gets nothing, and the text goes to a
/// terminal nobody is reading. Measured over SSH, where every command is piped - the exit
/// codes were right and not one line came back. So a handle that is already there is left
/// alone, and the console is only borrowed when there is nothing at all.
#[cfg(windows)]
fn attach_console() {
    use std::os::windows::io::RawHandle;

    const ATTACH_PARENT_PROCESS: u32 = 0xFFFF_FFFF;
    const GENERIC_READ: u32 = 0x8000_0000;
    const GENERIC_WRITE: u32 = 0x4000_0000;
    const FILE_SHARE_READ: u32 = 0x0000_0001;
    const FILE_SHARE_WRITE: u32 = 0x0000_0002;
    const OPEN_EXISTING: u32 = 3;
    const STD_INPUT_HANDLE: u32 = 0xFFFF_FFF6; // -10
    const STD_OUTPUT_HANDLE: u32 = 0xFFFF_FFF5; // -11
    const STD_ERROR_HANDLE: u32 = 0xFFFF_FFF4; // -12
    const INVALID_HANDLE_VALUE: RawHandle = usize::MAX as RawHandle;

    #[link(name = "kernel32")]
    extern "system" {
        fn AttachConsole(dwProcessId: u32) -> i32;
        fn GetStdHandle(nStdHandle: u32) -> RawHandle;
        fn CreateFileW(
            lpFileName: *const u16,
            dwDesiredAccess: u32,
            dwShareMode: u32,
            lpSecurityAttributes: *mut core::ffi::c_void,
            dwCreationDisposition: u32,
            dwFlagsAndAttributes: u32,
            hTemplateFile: RawHandle,
        ) -> RawHandle;
        fn SetStdHandle(nStdHandle: u32, hHandle: RawHandle) -> i32;
    }

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    unsafe {
        // Each handle is asked about separately. Probing stdout and then repointing both
        // was the same bug one stream over: a caller that supplies a good stderr and no
        // stdout would have had every error message moved into a console it is not
        // reading, and the error messages are the whole reason a non-zero exit is useful.
        let missing = |h: RawHandle| h.is_null() || h == INVALID_HANDLE_VALUE;
        let want_out = missing(GetStdHandle(STD_OUTPUT_HANDLE));
        let want_err = missing(GetStdHandle(STD_ERROR_HANDLE));
        if !want_out && !want_err {
            return;
        }
        if AttachConsole(ATTACH_PARENT_PROCESS) == 0 {
            // No parent console - launched from Explorer, or already attached. Either way
            // there is nowhere to print, and failing loudly here would only replace silence
            // with a dialog nobody asked for.
            return;
        }
        let out = wide("CONOUT$");
        let inp = wide("CONIN$");
        let h_out = CreateFileW(
            out.as_ptr(), GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, std::ptr::null_mut(), OPEN_EXISTING, 0,
            std::ptr::null_mut(),
        );
        if h_out != INVALID_HANDLE_VALUE {
            if want_out {
                SetStdHandle(STD_OUTPUT_HANDLE, h_out);
            }
            if want_err {
                SetStdHandle(STD_ERROR_HANDLE, h_out);
            }
        }
        let h_in = CreateFileW(
            inp.as_ptr(), GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, std::ptr::null_mut(), OPEN_EXISTING, 0,
            std::ptr::null_mut(),
        );
        if h_in != INVALID_HANDLE_VALUE {
            SetStdHandle(STD_INPUT_HANDLE, h_in);
        }
    }
}

/// Nothing to attach to: a Unix process already has whatever console started it.
#[cfg(not(windows))]
fn attach_console() {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_the_two_commands_are_taken() {
        let a = |s: &str| vec![s.to_string()];
        assert!(try_run(&a("--export-track")).is_some());
        assert!(try_run(&a("--check-catalog")).is_some());
        // Anything else belongs to Tauri or the webview. Swallowing it here would break
        // the window to serve the command line.
        assert!(try_run(&a("--some-tauri-flag")).is_none());
        assert!(try_run(&[]).is_none());
    }

    #[test]
    fn file_names_keep_everything_a_filename_can_hold() {
        // Kept, so that two rows that differ only in these characters do not land on one
        // file and silently overwrite each other.
        assert_eq!(safe_file_name("VDGS FDF 2026-08-22"), "VDGS FDF 2026-08-22");
        assert_eq!(safe_file_name("Sols+Street+League+1"), "Sols+Street+League+1");
        assert_eq!(safe_file_name("日本語"), "日本語");
        // Replaced, because a filename cannot hold them.
        assert_eq!(safe_file_name("a/b\\c:d"), "a-b-c-d");
        assert_eq!(safe_file_name("q?*|<>\""), "q------");
    }
}
