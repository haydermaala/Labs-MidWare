//! Technician desktop shell (Tauri 2). Phase 1: opens a window that loads the
//! React frontend. No IPC commands to the gateway daemon yet; the UI is not the
//! communication process.
#![forbid(unsafe_code)]

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
