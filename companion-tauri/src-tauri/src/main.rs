#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::io::Write;

fn main() {
    // Checked before Tauri starts, because the two commands here must not open a window
    // and must not need one. An argument this does not recognise falls through to `run`,
    // so Tauri and the webview keep their own flags.
    let args: Vec<String> = std::env::args().skip(1).collect();
    if let Some(code) = companion_lib::cli::try_run(&args) {
        // Flushed by hand: `process::exit` runs no destructors, and Rust block-buffers
        // stdout whenever it is not a terminal. Redirect the command anywhere - a pipe, a
        // file, a CI log - and the buffer is discarded on the way out. Measured on Windows,
        // and it is the worst shape of bug to leave in a CLI: the exit code is right, the
        // work is done, and the output is simply gone. Interactively it looks fine, because
        // a terminal gets line buffering.
        let _ = std::io::stdout().flush();
        let _ = std::io::stderr().flush();
        std::process::exit(code);
    }
    companion_lib::run()
}
