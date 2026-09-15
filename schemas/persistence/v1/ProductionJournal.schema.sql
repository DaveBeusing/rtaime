-- Copyright (c) Dave Beusing <david.beusing@gmail.com>.
-- Production Journal schema v1. This is a separate durability lane from management persistence.

CREATE TABLE IF NOT EXISTS schema_metadata(
	component TEXT PRIMARY KEY,
	schema_version INTEGER NOT NULL
);

INSERT OR IGNORE INTO schema_metadata(component, schema_version)
VALUES('production-journal', 1);

CREATE TABLE IF NOT EXISTS production_journal(
	ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
	event_id TEXT NOT NULL UNIQUE,
	production_id TEXT NOT NULL,
	authoritative_revision TEXT NOT NULL,
	timestamp_utc TEXT NOT NULL,
	category TEXT NOT NULL,
	code TEXT NOT NULL,
	detail TEXT NOT NULL,
	causation_id TEXT NULL,
	failure_code TEXT NULL,
	failure_message TEXT NULL,
	event_checksum TEXT NOT NULL,
	previous_hash TEXT NOT NULL,
	entry_hash TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_production_journal_production_ordinal
	ON production_journal(production_id, ordinal);

CREATE TABLE IF NOT EXISTS production_journal_head(
	singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
	last_ordinal INTEGER NOT NULL,
	last_hash TEXT NOT NULL
);

INSERT OR IGNORE INTO production_journal_head(singleton_id, last_ordinal, last_hash)
VALUES(1, 0, '0000000000000000000000000000000000000000000000000000000000000000');
