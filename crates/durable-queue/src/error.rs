//! Error type for the edge store.

use thiserror::Error;

/// Errors from the edge persistence layer.
#[derive(Debug, Error)]
pub enum StoreError {
    /// Underlying SQLite error.
    #[error("sqlite: {0}")]
    Sqlite(#[from] rusqlite::Error),

    /// A migration target was requested that does not exist.
    #[error("unknown migration version {requested}; latest is {latest}")]
    UnknownMigration {
        /// The requested target version.
        requested: u32,
        /// The latest available version.
        latest: u32,
    },

    /// A row that was expected to exist was not found.
    #[error("not found: {0}")]
    NotFound(String),

    /// A timestamp could not be formatted/parsed.
    #[error("time formatting: {0}")]
    Time(String),
}

/// Convenience result alias.
pub type Result<T> = core::result::Result<T, StoreError>;
