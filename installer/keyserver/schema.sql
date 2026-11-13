-- Unlock server database (D1). Apply with: npx wrangler d1 execute btucu-unlock --remote --file schema.sql
-- Codes are stored as SHA-256 of the normalized code (upper case, letters and digits only), never the code itself.
CREATE TABLE IF NOT EXISTS codes (
  hash TEXT PRIMARY KEY,
  name TEXT NOT NULL UNIQUE,
  created INTEGER NOT NULL,
  revoked INTEGER NOT NULL DEFAULT 0
);

-- Releases the server refuses (32 hex release id, lower case).
CREATE TABLE IF NOT EXISTS retired (
  release TEXT PRIMARY KEY,
  at INTEGER NOT NULL
);

-- open = 'false' switches installing off; closed_message is shown to players.
CREATE TABLE IF NOT EXISTS settings (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

-- Every unlock attempt with a well-formed request. code_hash is a code's hash, or "steam:" + a Steam account's hash. No IP
-- address is stored.
CREATE TABLE IF NOT EXISTS unlocks (
  code_hash TEXT NOT NULL,
  release TEXT NOT NULL,
  at INTEGER NOT NULL,
  ok INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS unlocks_by_code ON unlocks (code_hash, at);

-- Steam accounts that unlocked through Steam, or that are blocked. hash = SHA-256 of "btucu-steam|" + the 64-bit Steam ID,
-- so the table holds no Steam ID in the clear. borrowed = the first unlock came through Family Sharing.
CREATE TABLE IF NOT EXISTS steam (
  hash TEXT PRIMARY KEY,
  first INTEGER NOT NULL,
  borrowed INTEGER NOT NULL DEFAULT 0,
  blocked INTEGER NOT NULL DEFAULT 0
);
