//! Edge SQLite persistence for the gateway daemon.
//!
//! Provides the durable local store: append-only raw messages, normalized result
//! sets linked to their raw evidence (provenance), a deduplicating outbox,
//! acknowledgements, dead-letters, an append-only audit trail, and config —
//! plus a reversible migration runner. See `docs/architecture/canonical-model-v0.1.md`
//! and DEVELOPMENT_PLAN.md Phase 2.
//!
//! SQLite is bundled (no system dependency). Foreign keys are enforced, so a
//! normalized result set can never exist without a raw message to trace to.
#![forbid(unsafe_code)]

mod error;
mod migrations;
mod store;

pub use error::{Result, StoreError};
pub use migrations::{current_version, latest_version, migrate_to, migrate_to_latest, Migration};
pub use store::{Enqueue, PendingDelivery, RawMessageMeta, RawMessageRecord, Store};

/// Semantic version of this crate, surfaced for provenance/audit.
pub const CRATE_VERSION: &str = env!("CARGO_PKG_VERSION");

/// Returns the crate name; retained for provenance/telemetry labelling.
pub fn crate_name() -> &'static str {
    env!("CARGO_PKG_NAME")
}

#[cfg(test)]
mod tests {
    use super::*;
    use rusqlite::Connection;

    #[test]
    fn reports_identity() {
        assert!(!CRATE_VERSION.is_empty());
        assert_eq!(crate_name(), "durable-queue");
    }

    #[test]
    fn migrations_upgrade_and_rollback() {
        let mut conn = Connection::open_in_memory().unwrap();
        assert_eq!(current_version(&conn).unwrap(), 0);

        migrate_to_latest(&mut conn).unwrap();
        assert_eq!(current_version(&conn).unwrap(), latest_version());
        // Schema exists after upgrade.
        let tables: i64 = conn
            .query_row(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='outbox'",
                [],
                |r| r.get(0),
            )
            .unwrap();
        assert_eq!(tables, 1);

        // Roll all the way back to empty.
        migrate_to(&mut conn, 0).unwrap();
        assert_eq!(current_version(&conn).unwrap(), 0);
        let tables: i64 = conn
            .query_row(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='outbox'",
                [],
                |r| r.get(0),
            )
            .unwrap();
        assert_eq!(tables, 0, "down migration must drop tables");

        // Re-upgrade works after rollback.
        migrate_to_latest(&mut conn).unwrap();
        assert_eq!(current_version(&conn).unwrap(), latest_version());
    }

    #[test]
    fn unknown_migration_target_errors() {
        let mut conn = Connection::open_in_memory().unwrap();
        let err = migrate_to(&mut conn, 999).unwrap_err();
        assert!(matches!(err, StoreError::UnknownMigration { .. }));
    }

    #[test]
    fn raw_message_roundtrips_and_is_append_only_evidence() {
        let store = Store::open_in_memory().unwrap();
        let id = store
            .insert_raw_message("serial", b"H|\\^&|payload", Some("dev-1"), Some("crc:ok"))
            .unwrap();
        let got = store.get_raw_message(id).unwrap().expect("present");
        assert_eq!(got.id, id);
        assert_eq!(got.payload, b"H|\\^&|payload");
        assert_eq!(got.byte_len, 13);
        assert_eq!(got.transport, "serial");
        assert_eq!(got.encryption, "none");
    }

    #[test]
    fn result_set_requires_existing_raw_message() {
        let store = Store::open_in_memory().unwrap();
        // Fabricate an id that was never inserted; FK must reject it.
        let orphan = canonical_model::RawMessageId::new();
        let err = store.insert_result_set(orphan, b"{}").unwrap_err();
        assert!(
            matches!(err, StoreError::Sqlite(_)),
            "foreign key must reject an orphan result set"
        );

        // With a real raw message, it succeeds and links back (provenance).
        let raw = store
            .insert_raw_message("tcp", b"bytes", None, None)
            .unwrap();
        let rs = store.insert_result_set(raw, b"{\"results\":[]}").unwrap();
        let linked: String = store
            .connection()
            .query_row(
                "SELECT raw_message_id FROM result_sets WHERE id = ?1",
                [rs.to_string()],
                |r| r.get(0),
            )
            .unwrap();
        assert_eq!(linked, raw.to_string());
    }

    #[test]
    fn outbox_deduplicates() {
        let mut store = Store::open_in_memory().unwrap();
        let first = store.enqueue("dedup-abc", None, b"payload").unwrap();
        let second = store.enqueue("dedup-abc", None, b"payload-again").unwrap();

        assert!(matches!(first, Enqueue::Inserted(_)));
        match (first, second) {
            (Enqueue::Inserted(a), Enqueue::Duplicate(b)) => assert_eq!(a, b),
            other => panic!("expected inserted then duplicate, got {other:?}"),
        }
        assert_eq!(store.outbox_count("pending").unwrap(), 1);
    }

    #[test]
    fn ack_marks_delivered_and_dead_letter_marks_dead() {
        let mut store = Store::open_in_memory().unwrap();
        let a = match store.enqueue("k1", None, b"p1").unwrap() {
            Enqueue::Inserted(id) => id,
            other => panic!("{other:?}"),
        };
        let b = match store.enqueue("k2", None, b"p2").unwrap() {
            Enqueue::Inserted(id) => id,
            other => panic!("{other:?}"),
        };

        store.record_ack(&a, "accepted", None).unwrap();
        assert_eq!(store.outbox_count("delivered").unwrap(), 1);

        store.dead_letter(&b, "max retries exceeded").unwrap();
        assert_eq!(store.outbox_count("dead").unwrap(), 1);
        assert_eq!(store.outbox_count("pending").unwrap(), 0);
    }

    #[test]
    fn ack_on_missing_outbox_errors() {
        let mut store = Store::open_in_memory().unwrap();
        let err = store.record_ack("nope", "accepted", None).unwrap_err();
        assert!(matches!(err, StoreError::NotFound(_)));
    }

    #[test]
    fn retention_prunes_old_raw_messages() {
        let store = Store::open_in_memory().unwrap();
        store
            .insert_raw_message("file", b"old", None, None)
            .unwrap();
        store
            .insert_raw_message("file", b"new", None, None)
            .unwrap();
        // Cutoff far in the future prunes everything; far in the past prunes nothing.
        assert_eq!(
            store
                .prune_raw_messages_before("1900-01-01T00:00:00Z")
                .unwrap(),
            0
        );
        let pruned = store
            .prune_raw_messages_before("9999-01-01T00:00:00Z")
            .unwrap();
        assert_eq!(pruned, 2);
    }

    #[test]
    fn config_and_audit_roundtrip() {
        let store = Store::open_in_memory().unwrap();
        assert_eq!(store.get_config("mode").unwrap(), None);
        store.set_config("mode", "passive_capture").unwrap();
        store.set_config("mode", "passive_capture").unwrap(); // upsert idempotent
        assert_eq!(
            store.get_config("mode").unwrap().as_deref(),
            Some("passive_capture")
        );

        store
            .append_audit("config.changed", Some("mode"), None)
            .unwrap();
        assert_eq!(store.audit_count().unwrap(), 1);
    }
}
