//! Passive watched-file capture.
//!
//! Some analyzers/middleware drop result files into a directory. This watcher
//! captures **complete, stable** files only, then archives them — it never
//! writes to the analyzer (capture-only; ADR 0009).
//!
//! Safety properties:
//! - **Stable-write detection**: a file is captured only after its size is
//!   unchanged across consecutive polls, so half-written files are ignored.
//! - **Atomic-ish ingestion**: temp/partial files (dotfiles, `.tmp`, `.part`,
//!   `.partial`, `.crdownload`) are skipped until renamed into place.
//! - **Path/extension allowlist**: only permitted files are considered.
//! - **Bounded**: files larger than `max_file_bytes` are quarantined, not read.
//! - **Idempotent**: captured files are moved to an archive dir, so they are
//!   never processed twice.
//! - **Quarantine**: unreadable/oversized files are moved aside for inspection.
#![forbid(unsafe_code)]

use std::collections::HashMap;
use std::fs::{self, File};
use std::path::{Path, PathBuf};

use transport_core::{
    capture_reader, CaptureConfig, CaptureError, CaptureSink, Result, TransportStats,
};

/// Semantic version of this crate, surfaced for provenance/audit.
pub const CRATE_VERSION: &str = env!("CARGO_PKG_VERSION");

const TEMP_SUFFIXES: &[&str] = &[".tmp", ".part", ".partial", ".crdownload", ".swp"];

/// Configuration for the watched-file transport.
#[derive(Debug, Clone)]
pub struct FileCaptureConfig {
    /// Directory to watch for inbound files.
    pub watch_dir: PathBuf,
    /// Allowed lowercase extensions (without dot). Empty means allow any
    /// non-temp file.
    pub allowed_extensions: Vec<String>,
    /// Files larger than this are quarantined rather than read (bound).
    pub max_file_bytes: u64,
    /// Where successfully captured files are moved (required for idempotency).
    pub archive_dir: PathBuf,
    /// Where unreadable/oversized files are moved.
    pub quarantine_dir: PathBuf,
    /// Byte-level capture bounds.
    pub capture: CaptureConfig,
}

impl FileCaptureConfig {
    fn extension_allowed(&self, path: &Path) -> bool {
        if self.allowed_extensions.is_empty() {
            return true;
        }
        match path.extension().and_then(|e| e.to_str()) {
            Some(ext) => self
                .allowed_extensions
                .iter()
                .any(|allowed| allowed.eq_ignore_ascii_case(ext)),
            None => false,
        }
    }
}

fn is_temp_name(name: &str) -> bool {
    name.starts_with('.')
        || TEMP_SUFFIXES
            .iter()
            .any(|s| name.to_ascii_lowercase().ends_with(s))
}

fn move_into(dir: &Path, file: &Path) -> std::io::Result<PathBuf> {
    fs::create_dir_all(dir)?;
    let name = file.file_name().expect("file has a name");
    let dest = dir.join(name);
    fs::rename(file, &dest)?;
    Ok(dest)
}

/// A stateful watched-file capturer. Call [`poll_once`] on an interval (or use
/// [`run`]).
///
/// [`poll_once`]: FileWatcher::poll_once
/// [`run`]: FileWatcher::run
pub struct FileWatcher {
    config: FileCaptureConfig,
    /// Last-observed size per file name, to detect stable writes.
    last_sizes: HashMap<String, u64>,
}

impl FileWatcher {
    /// Create a watcher for the given configuration.
    #[must_use]
    pub fn new(config: FileCaptureConfig) -> Self {
        Self {
            config,
            last_sizes: HashMap::new(),
        }
    }

    /// Scan the watch directory once. Files whose size is unchanged since the
    /// previous poll are captured (and archived); newly-seen or still-changing
    /// files are recorded and left for a later poll. Returns the number of files
    /// captured this poll.
    pub fn poll_once(&mut self, sink: &CaptureSink, stats: &TransportStats) -> Result<usize> {
        let entries = match fs::read_dir(&self.config.watch_dir) {
            Ok(e) => e,
            Err(e) => return Err(CaptureError::Io(e.to_string())),
        };

        // Gather current candidate files and their sizes.
        let mut current: Vec<(String, PathBuf, u64)> = Vec::new();
        for entry in entries.flatten() {
            let path = entry.path();
            let Some(name) = path.file_name().and_then(|n| n.to_str()).map(str::to_owned) else {
                continue;
            };
            let Ok(meta) = entry.metadata() else {
                continue;
            };
            if !meta.is_file() || is_temp_name(&name) || !self.config.extension_allowed(&path) {
                continue;
            }
            current.push((name, path, meta.len()));
        }

        let mut captured = 0usize;
        for (name, path, size) in current {
            match self.last_sizes.get(&name) {
                Some(&prev) if prev == size => {
                    // Stable across two polls → capture it.
                    self.last_sizes.remove(&name);
                    if self.process_file(&name, &path, size, sink, stats)? {
                        captured += 1;
                    }
                }
                _ => {
                    // New or still changing; remember size and wait.
                    self.last_sizes.insert(name, size);
                }
            }
        }
        Ok(captured)
    }

    fn process_file(
        &self,
        name: &str,
        path: &Path,
        size: u64,
        sink: &CaptureSink,
        stats: &TransportStats,
    ) -> Result<bool> {
        if size > self.config.max_file_bytes {
            stats.add_oversized_dropped();
            let _ = move_into(&self.config.quarantine_dir, path);
            return Ok(false);
        }

        let file = match File::open(path) {
            Ok(f) => f,
            Err(e) => {
                stats.add_read_error();
                let _ = move_into(&self.config.quarantine_dir, path);
                return Err(CaptureError::Io(e.to_string()));
            }
        };

        stats.add_connection(); // one captured file == one capture unit
        let source = format!("file:{name}");
        match capture_reader(file, &source, &self.config.capture, sink, stats) {
            Ok(()) => {
                stats.add_disconnect();
                // Archive so the file is never captured again (idempotency).
                move_into(&self.config.archive_dir, path)
                    .map_err(|e| CaptureError::Io(e.to_string()))?;
                Ok(true)
            }
            Err(e) => {
                stats.add_disconnect();
                let _ = move_into(&self.config.quarantine_dir, path);
                Err(e)
            }
        }
    }

    /// Run the watcher, polling every `interval`, until `should_stop` returns true.
    pub fn run(
        &mut self,
        interval: std::time::Duration,
        sink: &CaptureSink,
        stats: &TransportStats,
        mut should_stop: impl FnMut() -> bool,
    ) {
        while !should_stop() {
            let _ = self.poll_once(sink, stats);
            std::thread::sleep(interval);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use std::sync::atomic::{AtomicU64, Ordering};

    // Unique temp dir under the OS temp, no external deps.
    fn temp_root() -> PathBuf {
        static COUNTER: AtomicU64 = AtomicU64::new(0);
        let n = COUNTER.fetch_add(1, Ordering::Relaxed);
        let dir =
            std::env::temp_dir().join(format!("labconnect-file-test-{}-{n}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    struct Dirs {
        root: PathBuf,
        watch: PathBuf,
    }
    impl Dirs {
        fn new() -> Self {
            let root = temp_root();
            let watch = root.join("inbox");
            fs::create_dir_all(&watch).unwrap();
            Self { root, watch }
        }
        fn config(&self) -> FileCaptureConfig {
            FileCaptureConfig {
                watch_dir: self.watch.clone(),
                allowed_extensions: vec!["dat".into()],
                max_file_bytes: 1024,
                archive_dir: self.root.join("archive"),
                quarantine_dir: self.root.join("quarantine"),
                capture: CaptureConfig::default(),
            }
        }
        fn write(&self, name: &str, bytes: &[u8]) -> PathBuf {
            let p = self.watch.join(name);
            let mut f = File::create(&p).unwrap();
            f.write_all(bytes).unwrap();
            f.sync_all().unwrap();
            p
        }
    }
    impl Drop for Dirs {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.root);
        }
    }

    #[test]
    fn captures_stable_file_after_two_polls_and_archives() {
        let dirs = Dirs::new();
        let (sink, rx) = CaptureSink::bounded(16);
        let stats = TransportStats::default();
        let mut w = FileWatcher::new(dirs.config());
        dirs.write("result1.dat", b"H|1|glucose|5.30");

        // First poll records the size; nothing captured yet (stable-write guard).
        assert_eq!(w.poll_once(&sink, &stats).unwrap(), 0);
        // Second poll: size unchanged → capture.
        assert_eq!(w.poll_once(&sink, &stats).unwrap(), 1);

        let got: Vec<u8> = std::iter::from_fn(|| rx.try_recv().ok())
            .flat_map(|c| c.bytes)
            .collect();
        assert_eq!(got, b"H|1|glucose|5.30");
        // Source file is gone from inbox and now in archive.
        assert!(!dirs.watch.join("result1.dat").exists());
        assert!(dirs.root.join("archive").join("result1.dat").exists());
    }

    #[test]
    fn ignores_temp_and_disallowed_files() {
        let dirs = Dirs::new();
        let (sink, _rx) = CaptureSink::bounded(16);
        let stats = TransportStats::default();
        let mut w = FileWatcher::new(dirs.config());
        dirs.write("partial.dat.part", b"still writing");
        dirs.write("notes.txt", b"wrong extension");
        dirs.write(".hidden.dat", b"dotfile");

        assert_eq!(w.poll_once(&sink, &stats).unwrap(), 0);
        assert_eq!(w.poll_once(&sink, &stats).unwrap(), 0);
        // None were tracked or captured.
        assert!(dirs.watch.join("partial.dat.part").exists());
        assert!(dirs.watch.join("notes.txt").exists());
    }

    #[test]
    fn does_not_capture_while_file_is_still_growing() {
        let dirs = Dirs::new();
        let (sink, _rx) = CaptureSink::bounded(16);
        let stats = TransportStats::default();
        let mut w = FileWatcher::new(dirs.config());

        dirs.write("growing.dat", b"part1");
        assert_eq!(w.poll_once(&sink, &stats).unwrap(), 0); // record size 5
        dirs.write("growing.dat", b"part1-and-more"); // size changed
        assert_eq!(w.poll_once(&sink, &stats).unwrap(), 0); // still not stable
        assert_eq!(w.poll_once(&sink, &stats).unwrap(), 1); // now stable → captured
    }

    #[test]
    fn oversized_file_is_quarantined_not_captured() {
        let dirs = Dirs::new();
        let (sink, rx) = CaptureSink::bounded(16);
        let stats = TransportStats::default();
        let mut cfg = dirs.config();
        cfg.max_file_bytes = 4;
        let mut w = FileWatcher::new(cfg);
        dirs.write("big.dat", b"way too many bytes");

        w.poll_once(&sink, &stats).unwrap();
        assert_eq!(w.poll_once(&sink, &stats).unwrap(), 0);
        assert!(
            rx.try_recv().is_err(),
            "oversized file must not be captured"
        );
        assert!(dirs.root.join("quarantine").join("big.dat").exists());
        assert_eq!(stats.snapshot().oversized_dropped, 1);
    }

    #[test]
    fn idempotent_no_recapture_after_archive() {
        let dirs = Dirs::new();
        let (sink, _rx) = CaptureSink::bounded(16);
        let stats = TransportStats::default();
        let mut w = FileWatcher::new(dirs.config());
        dirs.write("once.dat", b"data");

        w.poll_once(&sink, &stats).unwrap();
        assert_eq!(w.poll_once(&sink, &stats).unwrap(), 1);
        // Subsequent polls find nothing new to capture.
        assert_eq!(w.poll_once(&sink, &stats).unwrap(), 0);
        assert_eq!(w.poll_once(&sink, &stats).unwrap(), 0);
        assert_eq!(stats.snapshot().connections, 1);
    }
}
