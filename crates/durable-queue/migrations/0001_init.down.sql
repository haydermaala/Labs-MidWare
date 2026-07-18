-- Reverse of 0001_init.up.sql. Drop in reverse dependency order.
DROP TABLE IF EXISTS audit_events;
DROP TABLE IF EXISTS dead_letters;
DROP TABLE IF EXISTS acknowledgements;
DROP TABLE IF EXISTS outbox;
DROP TABLE IF EXISTS dedup_keys;
DROP TABLE IF EXISTS result_sets;
DROP TABLE IF EXISTS raw_messages;
DROP TABLE IF EXISTS config;
