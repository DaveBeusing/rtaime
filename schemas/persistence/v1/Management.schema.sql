-- Copyright (c) Dave Beusing <david.beusing@gmail.com>.
-- Management persistence schema v1. The executable migration/bootstrap remains authoritative.

CREATE TABLE IF NOT EXISTS schema_metadata(
	component TEXT PRIMARY KEY,
	schema_version INTEGER NOT NULL
);

INSERT OR IGNORE INTO schema_metadata(component, schema_version)
VALUES('management', 1);

CREATE TABLE IF NOT EXISTS management_documents(
	area TEXT NOT NULL,
	document_key TEXT NOT NULL,
	version TEXT NOT NULL,
	updated_utc TEXT NOT NULL,
	payload_json TEXT NOT NULL,
	checksum_sha256 TEXT NOT NULL,
	PRIMARY KEY(area, document_key)
);

CREATE TABLE IF NOT EXISTS production_checkpoints(
	checkpoint_id TEXT PRIMARY KEY,
	production_id TEXT NOT NULL,
	authoritative_revision TEXT NOT NULL,
	created_utc TEXT NOT NULL,
	format TEXT NOT NULL,
	payload BLOB NOT NULL,
	checksum_sha256 TEXT NOT NULL,
	UNIQUE(production_id, authoritative_revision)
);

CREATE INDEX IF NOT EXISTS ix_production_checkpoints_latest
	ON production_checkpoints(production_id, authoritative_revision DESC);
