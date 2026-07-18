-- Edge store schema v1.
-- Design notes:
--  * Raw messages are append-only evidence; do not UPDATE/DELETE except via
--    explicit, audited retention pruning.
--  * Every normalized result set links back to the raw message it came from
--    (provenance root). Foreign keys are enforced (PRAGMA foreign_keys = ON).
--  * Outbox deduplicates on `dedup_key` (UNIQUE) for idempotent delivery.
--  * No PHI or result values are stored in any index/label used for metrics.

-- Gateway configuration (key/value; small, non-secret operational settings).
CREATE TABLE config (
    key        TEXT PRIMARY KEY,
    value      TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;

-- Append-only raw device messages. `payload` holds the exact received bytes.
-- `encryption` records the at-rest scheme applied to `payload` ('none' until an
-- at-rest encryption decision is made; see SEC data-classification, OPEN).
CREATE TABLE raw_messages (
    id                 TEXT PRIMARY KEY,
    device_instance_id TEXT,
    transport          TEXT NOT NULL,
    received_at        TEXT NOT NULL,
    payload            BLOB NOT NULL,
    byte_len           INTEGER NOT NULL,
    checksum           TEXT,
    encryption         TEXT NOT NULL DEFAULT 'none'
) STRICT;

CREATE INDEX idx_raw_messages_received_at ON raw_messages (received_at);

-- Normalized result sets, each linked to its originating raw message.
CREATE TABLE result_sets (
    id             TEXT PRIMARY KEY,
    raw_message_id TEXT NOT NULL,
    payload        BLOB NOT NULL, -- serialized canonical ResultSet (with provenance)
    created_at     TEXT NOT NULL,
    FOREIGN KEY (raw_message_id) REFERENCES raw_messages (id)
) STRICT;

CREATE INDEX idx_result_sets_raw ON result_sets (raw_message_id);

-- Deduplication ledger: first time a dedup key was observed.
CREATE TABLE dedup_keys (
    dedup_key      TEXT PRIMARY KEY,
    first_seen_at  TEXT NOT NULL,
    raw_message_id TEXT,
    FOREIGN KEY (raw_message_id) REFERENCES raw_messages (id)
) STRICT;

-- Durable outbox for downstream delivery. UNIQUE(dedup_key) prevents duplicate
-- enqueue. `state`: pending | in_flight | delivered | dead.
CREATE TABLE outbox (
    id              TEXT PRIMARY KEY,
    result_set_id   TEXT,
    dedup_key       TEXT NOT NULL UNIQUE,
    payload         BLOB NOT NULL,
    created_at      TEXT NOT NULL,
    state           TEXT NOT NULL DEFAULT 'pending',
    attempts        INTEGER NOT NULL DEFAULT 0,
    next_attempt_at TEXT,
    last_error      TEXT,
    FOREIGN KEY (result_set_id) REFERENCES result_sets (id)
) STRICT;

CREATE INDEX idx_outbox_state ON outbox (state, next_attempt_at);

-- Acknowledgements received from downstream systems.
CREATE TABLE acknowledgements (
    id          TEXT PRIMARY KEY,
    outbox_id   TEXT NOT NULL,
    received_at TEXT NOT NULL,
    status      TEXT NOT NULL, -- accepted | rejected | error
    detail      TEXT,
    FOREIGN KEY (outbox_id) REFERENCES outbox (id)
) STRICT;

-- Dead-letter queue for messages that exhausted retries or failed permanently.
CREATE TABLE dead_letters (
    id         TEXT PRIMARY KEY,
    outbox_id  TEXT,
    reason     TEXT NOT NULL,
    created_at TEXT NOT NULL,
    payload    BLOB
) STRICT;

-- Append-only audit trail for configuration, delivery, and security events.
CREATE TABLE audit_events (
    id      TEXT PRIMARY KEY,
    at      TEXT NOT NULL,
    kind    TEXT NOT NULL,
    subject TEXT,
    detail  TEXT
) STRICT;

CREATE INDEX idx_audit_at ON audit_events (at);
