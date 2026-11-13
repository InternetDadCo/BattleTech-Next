// Unlock server for BattleTech UCU v3 packages (../../docs/keyserver-design.md).
//
// POST /v1/unlock {"release": "<32 hex>", "steam": "<ticket hex>", "steamKind": "webapi"|"session"} -> {"share": "<64 hex>"}
//             or {"release": "<32 hex>", "code": "<access code>"}
// Steam route: the installer sends a ticket that Steam issued for BATTLETECH (STEAM_APP_ID), and this server asks Valve's
// Web API (AuthenticateUserTicket, with the STEAM_WEBAPI_KEY secret) to confirm it. Steam only issues tickets to an account
// with a licence for the app, and binds each to the app and to STEAM_IDENTITY. No profile is read, so privacy settings
// don't matter. Codes are the manual route for anyone Steam can't confirm.
// The share is HMAC-SHA256(MASTER_SECRET, "btucu-share|" + release id); the packer computes the same with the same secret,
// so no release needs uploading here. D1 holds hashed codes, hashed Steam IDs, retired releases, settings and a record of
// unlocks. No IP address is stored: the per-IP limit is Cloudflare's rate-limiting binding, held in memory.

const PER_CODE_PER_DAY = 30;
const CODE_LENGTH = 20;
const STEAM_IDENTITY = "btucu-unlock";
const STEAM_AUTH = "https://api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1/";

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname !== "/v1/unlock") return json(404, { error: "not-found" });
    if (request.method !== "POST") return json(405, { error: "method" });
    if (!env.MASTER_SECRET || !/^[0-9a-fA-F]{64}$/.test(env.MASTER_SECRET.trim())) {
      return json(500, { error: "server", message: "The unlock server isn't set up yet." });
    }

    if (env.LIMITER) {
      const ip = request.headers.get("CF-Connecting-IP") || "unknown";
      const { success } = await env.LIMITER.limit({ key: ip });
      if (!success) return json(429, { error: "slow-down" });
    }

    let body;
    try {
      body = await request.json();
    } catch {
      return json(400, { error: "bad-request" });
    }
    const release = String((body && body.release) || "").toLowerCase();
    if (!/^[0-9a-f]{32}$/.test(release)) return json(400, { error: "bad-request" });
    const steam = String((body && body.steam) || "").toLowerCase();
    if (steam) {
      if (!/^[0-9a-f]{40,8192}$/.test(steam) || steam.length % 2) return json(400, { error: "bad-request" });
      return steamUnlock(env, release, steam, body.steamKind === "session" ? "session" : "webapi");
    }
    const code = normalize(body && body.code);
    if (code.length !== CODE_LENGTH) return json(400, { error: "bad-request" });

    const settings = await readSettings(env);
    if (settings.open === "false") {
      return json(503, { error: "closed", message: settings.closed_message || "Installing is switched off for now." });
    }

    const now = Math.floor(Date.now() / 1000);
    const codeHash = await sha256Hex(code);
    const known = await env.DB.prepare("SELECT revoked FROM codes WHERE hash = ?").bind(codeHash).first();
    if (!known || known.revoked) {
      await record(env, codeHash, release, now, 0);
      return json(403, { error: "code" });
    }
    const retired = await env.DB.prepare("SELECT 1 AS r FROM retired WHERE release = ?").bind(release).first();
    if (retired) {
      await record(env, codeHash, release, now, 0);
      return json(410, { error: "release" });
    }
    const used = await env.DB.prepare("SELECT COUNT(*) AS n FROM unlocks WHERE code_hash = ? AND ok = 1 AND at > ?")
      .bind(codeHash, now - 86400)
      .first();
    if (used && used.n >= PER_CODE_PER_DAY) return json(429, { error: "slow-down" });

    const share = await hmacHex(env.MASTER_SECRET.trim(), "btucu-share|" + release);
    await record(env, codeHash, release, now, 1);
    return json(200, { share });
  },
};

/** The Steam route: Valve confirms the ticket, then the same checks as a code, keyed on the hashed Steam ID. */
async function steamUnlock(env, release, ticket, kind) {
  const settings = await readSettings(env);
  if (settings.open === "false") {
    return json(503, { error: "closed", message: settings.closed_message || "Installing is switched off for now." });
  }
  if (settings.steam === "off" || !env.STEAM_WEBAPI_KEY) {
    return json(503, { error: "steam-off", message: settings.steam_message || "Unlocking through Steam is switched off for now. Enter an access code instead." });
  }

  const appId = String(env.STEAM_APP_ID || "637090");
  const q = new URLSearchParams({ key: env.STEAM_WEBAPI_KEY.trim(), appid: appId, ticket });
  if (kind === "webapi") q.set("identity", STEAM_IDENTITY);
  let answer;
  try {
    const r = await fetch(STEAM_AUTH + "?" + q.toString(), { headers: { "user-agent": "btucu-unlock" } });
    if (!r.ok) return json(502, { error: "steam-down", message: "Valve answered " + r.status + "." });
    answer = await r.json();
  } catch {
    return json(502, { error: "steam-down" });
  }
  const params = answer && answer.response && answer.response.params;
  if (!params || params.result !== "OK" || !/^\d{17}$/.test(String(params.steamid || ""))) {
    const err = answer && answer.response && answer.response.error;
    const why = err && err.errorcode === 102 ? "The Steam ticket was for another game." :
      err && err.errorcode === 100 ? "Steam said the ticket had expired." :
      "Steam didn't confirm that this account owns BATTLETECH" + (err && err.errordesc ? " (" + err.errordesc + ")" : "") + ".";
    return json(403, { error: "steam", message: why });
  }

  const now = Math.floor(Date.now() / 1000);
  const user = await sha256Hex("btucu-steam|" + params.steamid);
  const owner = params.ownersteamid && params.ownersteamid !== params.steamid ? await sha256Hex("btucu-steam|" + params.ownersteamid) : user;
  const key = "steam:" + user;
  const blocked = await env.DB.prepare("SELECT 1 AS b FROM steam WHERE hash IN (?, ?) AND blocked = 1").bind(user, owner).first();
  if (blocked) {
    await record(env, key, release, now, 0);
    return json(403, { error: "steam-blocked" });
  }
  if (owner !== user && settings.family_sharing === "deny") {
    await record(env, key, release, now, 0);
    return json(403, { error: "steam-borrowed" });
  }
  const retired = await env.DB.prepare("SELECT 1 AS r FROM retired WHERE release = ?").bind(release).first();
  if (retired) {
    await record(env, key, release, now, 0);
    return json(410, { error: "release" });
  }
  const used = await env.DB.prepare("SELECT COUNT(*) AS n FROM unlocks WHERE code_hash = ? AND ok = 1 AND at > ?").bind(key, now - 86400).first();
  if (used && used.n >= PER_CODE_PER_DAY) return json(429, { error: "slow-down" });

  await env.DB.prepare("INSERT OR IGNORE INTO steam (hash, first, borrowed) VALUES (?, ?, ?)").bind(user, now, owner !== user ? 1 : 0).run();
  const share = await hmacHex(env.MASTER_SECRET.trim(), "btucu-share|" + release);
  await record(env, key, release, now, 1);
  return json(200, { share });
}

/** Upper case, letters and digits only: dashes, spaces and anything else are ignored. Same rule as the installer. */
export function normalize(code) {
  return String(code || "").toUpperCase().replace(/[^A-Z0-9]/g, "");
}

async function readSettings(env) {
  const { results } = await env.DB.prepare("SELECT key, value FROM settings").all();
  const map = {};
  for (const r of results || []) map[r.key] = r.value;
  return map;
}

async function record(env, codeHash, release, at, ok) {
  await env.DB.prepare("INSERT INTO unlocks (code_hash, release, at, ok) VALUES (?, ?, ?, ?)").bind(codeHash, release, at, ok).run();
}

function json(status, value) {
  return new Response(JSON.stringify(value), { status, headers: { "content-type": "application/json", "cache-control": "no-store" } });
}

function hex(buffer) {
  return [...new Uint8Array(buffer)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function fromHex(s) {
  const b = new Uint8Array(s.length / 2);
  for (let i = 0; i < b.length; i++) b[i] = parseInt(s.substr(i * 2, 2), 16);
  return b;
}

async function sha256Hex(text) {
  return hex(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text)));
}

async function hmacHex(secretHex, message) {
  const key = await crypto.subtle.importKey("raw", fromHex(secretHex), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return hex(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(message)));
}
