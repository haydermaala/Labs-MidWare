//! Schema migrations for the edge SQLite store.
//!
//! A tiny, dependency-free runner keyed on SQLite's `PRAGMA user_version`. Each
//! migration has an `up` and a `down` so upgrade **and** rollback are testable
//! (Phase 2 acceptance). Migrations are append-only: never edit a released
//! migration; add a new one.

use rusqlite::Connection;

use crate::error::{Result, StoreError};

/// A single, reversible schema migration.
pub struct Migration {
    /// 1-based version this migration brings the schema *to*.
    pub version: u32,
    /// SQL applied to move from `version - 1` to `version`.
    pub up: &'static str,
    /// SQL applied to move from `version` back to `version - 1`.
    pub down: &'static str,
}

/// The ordered list of migrations. Index + 1 == version.
pub const MIGRATIONS: &[Migration] = &[Migration {
    version: 1,
    up: include_str!("../migrations/0001_init.up.sql"),
    down: include_str!("../migrations/0001_init.down.sql"),
}];

/// The latest schema version available in this build.
#[must_use]
pub fn latest_version() -> u32 {
    MIGRATIONS.len() as u32
}

/// Read the current schema version from the connection.
pub fn current_version(conn: &Connection) -> Result<u32> {
    let v: i64 = conn.query_row("PRAGMA user_version", [], |row| row.get(0))?;
    Ok(v as u32)
}

/// Migrate the database to `target` (0 == empty). Applies `up` migrations forward
/// or `down` migrations backward as needed, each in its own transaction.
pub fn migrate_to(conn: &mut Connection, target: u32) -> Result<()> {
    let latest = latest_version();
    if target > latest {
        return Err(StoreError::UnknownMigration {
            requested: target,
            latest,
        });
    }

    // Upgrade.
    while current_version(conn)? < target {
        let next = current_version(conn)? + 1;
        let m = &MIGRATIONS[(next - 1) as usize];
        let tx = conn.transaction()?;
        tx.execute_batch(m.up)?;
        // PRAGMA cannot be parameterized; version is a trusted internal counter.
        tx.pragma_update(None, "user_version", m.version as i64)?;
        tx.commit()?;
    }

    // Downgrade.
    while current_version(conn)? > target {
        let cur = current_version(conn)?;
        let m = &MIGRATIONS[(cur - 1) as usize];
        let tx = conn.transaction()?;
        tx.execute_batch(m.down)?;
        tx.pragma_update(None, "user_version", (cur - 1) as i64)?;
        tx.commit()?;
    }

    Ok(())
}

/// Migrate the database up to the latest available version.
pub fn migrate_to_latest(conn: &mut Connection) -> Result<()> {
    migrate_to(conn, latest_version())
}
