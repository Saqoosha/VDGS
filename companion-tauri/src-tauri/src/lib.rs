pub mod catalog;
pub mod game;
pub mod bepinex;
pub mod tracks;
pub mod launch;
pub mod state;
pub mod settings;

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
