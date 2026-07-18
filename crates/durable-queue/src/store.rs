//! The edge SQLite store: connection setup, migrations, and repositories.

use std::path::Path;

use canonical_model::{RawMessageId, ResultSetId};
use rusqlite::{params, Connection, OptionalExtension};
use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;
use uuid::Uuid;

use crate::error::{Result, StoreError};
use crate::migrations;

fn now_rfc3339() -> Result<String> {
    OffsetDateTime::now_utc()
        .format(&Rfc3339)
        .map_err(|e| StoreError::Time(e.to_string()))
}

/// A raw device message as stored (append-only evidence).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RawMessageRecord {
    /// Identity.
    pub id: RawMessageId,
    /// Device instance this arrived from, if known.
    pub device_instance_id: Option<String>,
    /// Transport it arrived over (e.g. "serial", "tcp", "file").
    pub transport: String,
    /// Receipt time (RFC 3339, UTC).
    pub received_at: String,
    /// Exact received bytes.
    pub payload: Vec<u8>,
    /// Byte length of `payload`.
    pub byte_len: i64,
    /// Optional checksum recorded at capture.
    pub checksum: Option<String>,
    /// At-rest encryption scheme for `payload` ("none" until decided).
    pub encryption: String,
}

/// Redaction-safe metadata for a raw message (no payload bytes).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RawMessageMeta {
    /// Identity.
    pub id: RawMessageId,
    /// Transport it arrived over.
    pub transport: String,
    /// Receipt time (RFC 3339, UTC).
    pub received_at: String,
    /// Byte length of the (unshown) payload.
    pub byte_len: i64,
}

/// Outcome of an outbox enqueue.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Enqueue {
    /// A new outbox row was created (its id).
    Inserted(String),
    /// The dedup key already existed; the existing outbox id is returned.
    Duplicate(String),
}

/// The edge persistence store.
pub struct Store {
    conn: Connection,
}

impl Store {
    /// Open (creating if needed) a file-backed store and migrate to the latest
    /// schema. Enables foreign keys and WAL.
    pub fn open(path: impl AsRef<Path>) -> Result<Self> {
        let conn = Connection::open(path)?;
        Self::from_connection(conn)
    }

    /// Open an in-memory store (for tests) migrated to the latest schema.
    pub fn open_in_memory() -> Result<Self> {
        let conn = Connection::open_in_memory()?;
        Self::from_connection(conn)
    }

    fn from_connection(mut conn: Connection) -> Result<Self> {
        conn.execute_batch(
            "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;",
        )?;
        migrations::migrate_to_latest(&mut conn)?;
        Ok(Self { conn })
    }

    /// Borrow the raw connection (for advanced/testing use).
    #[must_use]
    pub fn connection(&self) -> &Connection {
        &self.conn
    }

    /// The current schema version.
    pub fn schema_version(&self) -> Result<u32> {
        migrations::current_version(&self.conn)
    }

    // --- config ---------------------------------------------------------------

    /// Upsert a configuration value.
    pub fn set_config(&self, key: &str, value: &str) -> Result<()> {
        self.conn.execute(
            "INSERT INTO config (key, value, updated_at) VALUES (?1, ?2, ?3)
             ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
            params![key, value, now_rfc3339()?],
        )?;
        Ok(())
    }

    /// Read a configuration value, if present.
    pub fn get_config(&self, key: &str) -> Result<Option<String>> {
        Ok(self
            .conn
            .query_row("SELECT value FROM config WHERE key = ?1", [key], |r| {
                r.get(0)
            })
            .optional()?)
    }

    // --- raw messages (append-only) ------------------------------------------

    /// Persist a raw device message. Returns the new id. This is append-only:
    /// callers must not mutate raw messages after insert.
    pub fn insert_raw_message(
        &self,
        transport: &str,
        payload: &[u8],
        device_instance_id: Option<&str>,
        checksum: Option<&str>,
    ) -> Result<RawMessageId> {
        let id = RawMessageId::new();
        self.conn.execute(
            "INSERT INTO raw_messages
               (id, device_instance_id, transport, received_at, payload, byte_len, checksum)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![
                id.to_string(),
                device_instance_id,
                transport,
                now_rfc3339()?,
                payload,
                payload.len() as i64,
                checksum,
            ],
        )?;
        Ok(id)
    }

    /// Fetch a raw message by id.
    pub fn get_raw_message(&self, id: RawMessageId) -> Result<Option<RawMessageRecord>> {
        self.conn
            .query_row(
                "SELECT id, device_instance_id, transport, received_at, payload, byte_len, checksum, encryption
                 FROM raw_messages WHERE id = ?1",
                [id.to_string()],
                |r| {
                    let id_str: String = r.get(0)?;
                    Ok(RawMessageRecord {
                        id: parse_raw_id(&id_str),
                        device_instance_id: r.get(1)?,
                        transport: r.get(2)?,
                        received_at: r.get(3)?,
                        payload: r.get(4)?,
                        byte_len: r.get(5)?,
                        checksum: r.get(6)?,
                        encryption: r.get(7)?,
                    })
                },
            )
            .optional()
            .map_err(StoreError::from)
    }

    /// Delete raw messages received strictly before `cutoff_rfc3339` (retention).
    /// This is the only sanctioned mutation of raw messages; callers should audit it.
    pub fn prune_raw_messages_before(&self, cutoff_rfc3339: &str) -> Result<usize> {
        let n = self.conn.execute(
            "DELETE FROM raw_messages WHERE received_at < ?1",
            [cutoff_rfc3339],
        )?;
        Ok(n)
    }

    /// Return metadata for the most recent raw messages — **id, transport,
    /// received_at, byte_len only, never the payload**. This is the default,
    /// redaction-safe view for the local API/UI (raw bytes may contain PHI).
    pub fn recent_raw_message_meta(&self, limit: usize) -> Result<Vec<RawMessageMeta>> {
        let mut stmt = self.conn.prepare(
            "SELECT id, transport, received_at, byte_len FROM raw_messages
             ORDER BY received_at DESC, id DESC LIMIT ?1",
        )?;
        let rows = stmt.query_map([limit as i64], |r| {
            let id_str: String = r.get(0)?;
            Ok(RawMessageMeta {
                id: parse_raw_id(&id_str),
                transport: r.get(1)?,
                received_at: r.get(2)?,
                byte_len: r.get(3)?,
            })
        })?;
        let mut out = Vec::new();
        for row in rows {
            out.push(row?);
        }
        Ok(out)
    }

    // --- normalized result sets (provenance linkage) --------------------------

    /// Store a normalized result set, linked to its originating raw message.
    /// The foreign key guarantees provenance: a result set cannot exist without a
    /// raw message to trace back to.
    pub fn insert_result_set(
        &self,
        raw_message_id: RawMessageId,
        payload: &[u8],
    ) -> Result<ResultSetId> {
        let id = ResultSetId::new();
        self.conn.execute(
            "INSERT INTO result_sets (id, raw_message_id, payload, created_at)
             VALUES (?1, ?2, ?3, ?4)",
            params![
                id.to_string(),
                raw_message_id.to_string(),
                payload,
                now_rfc3339()?
            ],
        )?;
        Ok(id)
    }

    // --- outbox + dedup -------------------------------------------------------

    /// Enqueue a payload for downstream delivery, deduplicated by `dedup_key`.
    /// If the key was already enqueued, no new row is created and the existing
    /// outbox id is returned.
    pub fn enqueue(
        &mut self,
        dedup_key: &str,
        result_set_id: Option<ResultSetId>,
        payload: &[u8],
    ) -> Result<Enqueue> {
        if let Some(existing) = self
            .conn
            .query_row(
                "SELECT id FROM outbox WHERE dedup_key = ?1",
                [dedup_key],
                |r| r.get::<_, String>(0),
            )
            .optional()?
        {
            return Ok(Enqueue::Duplicate(existing));
        }

        let id = Uuid::new_v4().to_string();
        let now = now_rfc3339()?;
        let tx = self.conn.transaction()?;
        tx.execute(
            "INSERT INTO outbox (id, result_set_id, dedup_key, payload, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5)",
            params![
                id,
                result_set_id.map(|r| r.to_string()),
                dedup_key,
                payload,
                now
            ],
        )?;
        tx.execute(
            "INSERT OR IGNORE INTO dedup_keys (dedup_key, first_seen_at) VALUES (?1, ?2)",
            params![dedup_key, now],
        )?;
        tx.commit()?;
        Ok(Enqueue::Inserted(id))
    }

    /// Count outbox rows in a given state.
    pub fn outbox_count(&self, state: &str) -> Result<i64> {
        Ok(self.conn.query_row(
            "SELECT COUNT(*) FROM outbox WHERE state = ?1",
            [state],
            |r| r.get(0),
        )?)
    }

    /// Record an acknowledgement for an outbox row and mark it delivered when
    /// accepted.
    pub fn record_ack(
        &mut self,
        outbox_id: &str,
        status: &str,
        detail: Option<&str>,
    ) -> Result<()> {
        let ack_id = Uuid::new_v4().to_string();
        let now = now_rfc3339()?;
        let tx = self.conn.transaction()?;
        let affected = tx.execute(
            "INSERT INTO acknowledgements (id, outbox_id, received_at, status, detail)
             SELECT ?1, ?2, ?3, ?4, ?5 WHERE EXISTS (SELECT 1 FROM outbox WHERE id = ?2)",
            params![ack_id, outbox_id, now, status, detail],
        )?;
        if affected == 0 {
            return Err(StoreError::NotFound(format!("outbox {outbox_id}")));
        }
        if status == "accepted" {
            tx.execute(
                "UPDATE outbox SET state = 'delivered' WHERE id = ?1",
                [outbox_id],
            )?;
        }
        tx.commit()?;
        Ok(())
    }

    /// Move an outbox row to the dead-letter table.
    pub fn dead_letter(&mut self, outbox_id: &str, reason: &str) -> Result<()> {
        let dl_id = Uuid::new_v4().to_string();
        let now = now_rfc3339()?;
        let tx = self.conn.transaction()?;
        tx.execute(
            "INSERT INTO dead_letters (id, outbox_id, reason, created_at, payload)
             SELECT ?1, id, ?2, ?3, payload FROM outbox WHERE id = ?4",
            params![dl_id, reason, now, outbox_id],
        )?;
        tx.execute(
            "UPDATE outbox SET state = 'dead' WHERE id = ?1",
            [outbox_id],
        )?;
        tx.commit()?;
        Ok(())
    }

    // --- audit ----------------------------------------------------------------

    /// Append an audit event. Append-only; no updates/deletes.
    pub fn append_audit(
        &self,
        kind: &str,
        subject: Option<&str>,
        detail: Option<&str>,
    ) -> Result<()> {
        self.conn.execute(
            "INSERT INTO audit_events (id, at, kind, subject, detail) VALUES (?1, ?2, ?3, ?4, ?5)",
            params![
                Uuid::new_v4().to_string(),
                now_rfc3339()?,
                kind,
                subject,
                detail
            ],
        )?;
        Ok(())
    }

    /// Count audit events (for tests/telemetry; no PHI in this count).
    pub fn audit_count(&self) -> Result<i64> {
        Ok(self
            .conn
            .query_row("SELECT COUNT(*) FROM audit_events", [], |r| r.get(0))?)
    }
}

fn parse_raw_id(s: &str) -> RawMessageId {
    Uuid::parse_str(s)
        .map(RawMessageId::from_uuid)
        .unwrap_or_else(|_| RawMessageId::from_uuid(Uuid::nil()))
}
