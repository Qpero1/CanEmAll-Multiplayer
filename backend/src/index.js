// Can 'Em All Multiplayer - lobby / signaling / relay backend (Cloudflare Worker + Durable Object)
//
// Game traffic normally goes peer-to-peer (UDP hole punch) or through Cloudflare TURN.
// This Worker only:
//   1. hands out lobby codes,
//   2. passes small signaling messages between host and joiners (public endpoints etc.),
//   3. hands out short-lived TURN credentials to players who are in a live lobby,
//   4. offers a last-resort WebSocket relay for networks where UDP is fully blocked.
//
// See PROTOCOL.md for the message format.

import { DurableObject } from "cloudflare:workers";

const PROTO = 1;
const ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I/O/0/1 so codes are easy to read out
const CODE_LEN = 6;
const HARD_MAX_PLAYERS = 8;          // only used for the "max" shown by /v1/lobby; the host's game enforces the real limit
const MAX_SOCKETS = 40;              // abuse guard: lobby connections per lobby
const STALE_MS = 90_000;             // a client pings every 25 s; silent this long = dead connection
const MAX_TEXT_BYTES = 8 * 1024;     // signaling messages
const MAX_RELAY_BYTES = 2048;        // one LiteNetLib datagram + 2-byte header
// The WebSocket relay carries every game packet through this Durable Object, and every packet counts
// toward the plan's Durable Object request limit (100k/day on Free). Players now fall back to Cloudflare
// TURN instead, so the relay is off: a joiner whose network blocks both direct and TURN gets a clear error.
const RELAY_ENABLED = false;
const TURN_TTL_SECONDS = 24 * 60 * 60;   // upper bound; logins are revoked as soon as their player leaves
const REVOKE_GRACE_MS = 20 * 60 * 1000;   // lobby socket dropped without a clean close: the game may still be running, revoke later
const HOST_PEER = 0;

// ---------------------------------------------------------------- Worker entry

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    const parts = url.pathname.split("/").filter(Boolean);

    if (parts[0] !== "v1") return json({ error: "not_found" }, 404);

    if (parts[1] === "health" && parts.length === 2)
      return json({ ok: true, proto: PROTO, turn: !!(env.TURN_KEY_ID && env.TURN_KEY_API_TOKEN) });

    // Abuse guard: each lobby hands out TURN credentials (billed bandwidth), so cap how many
    // lobbies / joins / lookups one IP can start. Normal play is a handful per minute.
    if (env.LIMITER && ["lobby", "host", "join"].includes(parts[1])) {
      const ip = req.headers.get("CF-Connecting-IP") || "?";
      const { success } = await env.LIMITER.limit({ key: ip });
      if (!success) return json({ error: "rate_limited", msg: "Too many attempts, wait a minute and try again" }, 429);
    }

    // GET /v1/lobby/CODE  -> quick "does this code exist" check for the join UI
    if (parts[1] === "lobby" && parts.length === 3 && !isWebSocket(req)) {
      const code = normalizeCode(parts[2]);
      if (!code) return json({ error: "bad_code" }, 400);
      return lobbyStub(env, code).fetch(internal(req, "info", code));
    }

    if (!isWebSocket(req)) return json({ error: "expected_websocket" }, 426);

    // WS /v1/host?ver=0.31.2&name=Arkin&max=8
    if (parts[1] === "host" && parts.length === 2) {
      for (let i = 0; i < 8; i++) {
        const code = randomCode();
        const res = await lobbyStub(env, code, regionHint(req)).fetch(internal(req, "host", code));
        if (res.status !== 409) return res; // 409 = code already in use, try another
      }
      return json({ error: "no_code_available" }, 503);
    }

    // WS /v1/join/CODE?ver=0.31.2&name=Friend
    if (parts[1] === "join" && parts.length === 3) {
      const code = normalizeCode(parts[2]);
      if (!code) return json({ error: "bad_code" }, 400);
      return lobbyStub(env, code).fetch(internal(req, "join", code));
    }

    return json({ error: "not_found" }, 404);
  },
};

// ---------------------------------------------------------------- Lobby Durable Object
//
// One instance per lobby code. Uses the WebSocket Hibernation API so an idle lobby
// costs nothing; per-socket state lives in the socket attachment.
//   host attachment:   { role:"host", peer:0, code, ver, name, max }
//   joiner attachment: { role:"join", peer:N, name }

export class Lobby extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.env = env;
    // Keepalive without waking the object: client sends "ping", gets "pong".
    this.ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  async fetch(req) {
    const url = new URL(req.url);
    const action = url.searchParams.get("_action");
    const code = url.searchParams.get("_code");
    const ver = (url.searchParams.get("ver") || "").slice(0, 32);
    const name = cleanName(url.searchParams.get("name"));

    if (action === "info") {
      const host = this.host();
      if (!host) return json({ exists: false }, 404);
      const h = host.deserializeAttachment();
      return json({ exists: true, players: this.joiners().length + 1, max: h.max, ver: h.ver });
    }

    if (action === "host") {
      if (this.host()) return new Response("code in use", { status: 409 });
      await this.revokeAll(); // leftovers from a previous lobby with this code
      await this.ctx.storage.deleteAll(); // fresh lobby
      await this.ctx.storage.put("nextPeer", 1);

      let max = parseInt(url.searchParams.get("max") || "", 10);
      if (!(max >= 2 && max <= HARD_MAX_PLAYERS)) max = HARD_MAX_PLAYERS;

      const [client, server] = Object.values(new WebSocketPair());
      this.ctx.acceptWebSocket(server, ["host"]);
      const ice = await this.iceServers();
      server.serializeAttachment({ role: "host", peer: HOST_PEER, code, ver, name, max, turn: ice.users });
      send(server, { t: "hosted", proto: PROTO, code, peer: HOST_PEER, max, ice: ice.servers });
      return new Response(null, { status: 101, webSocket: client });
    }

    if (action === "join") {
      const [client, server] = Object.values(new WebSocketPair());
      // Accept first, then report errors as a message + close code, so the mod can show a readable reason.
      this.ctx.acceptWebSocket(server, ["join"]);
      const host = this.host();
      const fail = (err, msg, closeCode) => {
        send(server, { t: "error", error: err, msg });
        server.close(closeCode, err);
        return new Response(null, { status: 101, webSocket: client });
      };
      if (!host) return fail("not_found", "No lobby with that code. Check the code or ask the host to host again.", 4404);
      const h = host.deserializeAttachment();
      if (ver !== h.ver) return fail("version", `Mod version mismatch: host has v${h.ver}, you have v${ver || "?"}`, 4409);
      // "full" is decided by the host's game, which knows who is really playing. Here we only drop dead
      // connections and guard against floods.
      this.pruneStale();
      if (this.joiners().length + 1 >= MAX_SOCKETS) return fail("full", "Lobby is busy, try again in a minute", 4429);

      const peer = await this.allocPeer();
      if (peer < 0) return fail("full", "Session is full", 4429);
      const ice = await this.iceServers();
      server.serializeAttachment({ role: "join", peer, name, at: Date.now(), turn: ice.users });
      send(server, { t: "joined", proto: PROTO, code, peer, host: h.name, ice: ice.servers });
      send(host, { t: "peer-join", peer, name });
      return new Response(null, { status: 101, webSocket: client });
    }

    return new Response("bad request", { status: 400 });
  }

  // ---- incoming messages

  async webSocketMessage(ws, msg) {
    const me = ws.deserializeAttachment();
    if (!me) return;

    // Binary = relay datagram: [u16 BE peer][payload]
    if (typeof msg !== "string") {
      if (!RELAY_ENABLED) {
        if (me.role === "join" && !me.relayOff) {
          me.relayOff = true;
          ws.serializeAttachment(me);
          send(ws, { t: "error", error: "relay_off", msg: "Couldn't connect to the host: your network blocks direct and TURN connections. Try another network, or Direct IP with port forwarding." });
        }
        return;
      }
      const buf = new Uint8Array(msg);
      if (buf.length < 3 || buf.length > MAX_RELAY_BYTES) return;
      const to = me.role === "host" ? (buf[0] << 8) | buf[1] : HOST_PEER;
      const target = this.peerSocket(to);
      if (!target || target === ws) return;
      // Rewrite header to the sender's id (in place; we own this buffer).
      buf[0] = (me.peer >> 8) & 0xff;
      buf[1] = me.peer & 0xff;
      try { target.send(buf); } catch {}
      return;
    }

    if (msg.length > MAX_TEXT_BYTES) return;
    let m;
    try { m = JSON.parse(msg); } catch { return; }
    if (!m || typeof m.t !== "string") return;

    switch (m.t) {
      case "signal": {
        // Opaque payload between host and a joiner (endpoints, punch tokens, "use relay", ...).
        const to = me.role === "host" ? (m.to | 0) : HOST_PEER;
        const target = this.peerSocket(to);
        if (target && target !== ws) send(target, { t: "signal", from: me.peer, data: m.data });
        break;
      }
      case "kick": {
        if (me.role !== "host") break;
        const target = this.peerSocket(m.peer | 0);
        if (target && target !== ws) {
          send(target, { t: "error", error: "kicked", msg: String(m.reason || "Kicked by host").slice(0, 200) });
          const ta = target.deserializeAttachment();
          if (ta && ta.turn && ta.turn.length) { await this.revoke(ta.turn); ta.turn = []; target.serializeAttachment(ta); }
          target.close(4403, "kicked");
        }
        break;
      }
      case "config": {
        if (me.role !== "host") break;
        let max = m.max | 0;
        if (max >= 2 && max <= HARD_MAX_PLAYERS) { me.max = max; ws.serializeAttachment(me); }
        break;
      }
      case "ice": {
        // Refresh TURN credentials (e.g. long session). Only for sockets in a live lobby.
        if (!this.host()) break;
        if ((me.turn || []).length >= 4) break; // plenty for one session; stops credential farming
        const ice = await this.iceServers();
        me.turn = (me.turn || []).concat(ice.users);
        ws.serializeAttachment(me);
        send(ws, { t: "ice", ice: ice.servers });
        break;
      }
    }
  }

  async webSocketClose(ws, code, reason) {
    try { ws.close(code === 1005 ? 1000 : code, reason); } catch {}
    await this.left(ws, code === 1000 || code === 1005); // 1000/1005 = closed on purpose; 1006 etc. = dropped
  }

  async webSocketError(ws) {
    await this.left(ws, false);
  }

  /// clean = the game closed the lobby connection on purpose (left / stopped hosting).
  async left(ws, clean) {
    const me = ws.deserializeAttachment();
    if (!me) return;
    ws.serializeAttachment(null); // so a later close/error event doesn't run this twice
    if (me.role === "host") {
      const users = (me.turn || []).slice();
      for (const j of this.joiners()) {
        const a = j.deserializeAttachment();
        if (a && a.turn) users.push(...a.turn);
        j.serializeAttachment(null); // handled here, skip its own close event
        send(j, { t: "closed", reason: "Host closed the lobby" });
        try { j.close(4410, "host left"); } catch {}
      }
      if (clean) {
        // Session is over for everyone: kill every TURN login this lobby handed out.
        await this.revoke(users);
        await this.revokeAll();
        await this.ctx.storage.deleteAll();
      } else {
        // Host's lobby link dropped but their game may still be running: revoke a bit later.
        await this.ctx.storage.deleteAll();
        await this.revokeLater(users);
      }
    } else {
      const host = this.host();
      if (host) send(host, { t: "peer-leave", peer: me.peer });
      if (clean) await this.revoke(me.turn); else await this.revokeLater(me.turn);
    }
  }

  // ---- TURN login revocation

  async revoke(users) {
    const { TURN_KEY_ID, TURN_KEY_API_TOKEN } = this.env;
    if (!TURN_KEY_ID || !TURN_KEY_API_TOKEN || !users) return;
    await Promise.all(users.filter(Boolean).map((u) =>
      fetch(`https://rtc.live.cloudflare.com/v1/turn/keys/${TURN_KEY_ID}/credentials/${encodeURIComponent(u)}/revoke`, {
        method: "POST",
        headers: { Authorization: `Bearer ${TURN_KEY_API_TOKEN}` },
      }).catch(() => {})
    ));
  }

  async revokeLater(users) {
    if (!users || !users.length) return;
    const due = Date.now() + REVOKE_GRACE_MS;
    const list = (await this.ctx.storage.get("revoke")) || [];
    for (const u of users) list.push({ u, due });
    await this.ctx.storage.put("revoke", list);
    const cur = await this.ctx.storage.getAlarm();
    if (!cur || cur > due) await this.ctx.storage.setAlarm(due);
  }

  /// Revoke everything still pending (lobby fully over).
  async revokeAll() {
    const list = (await this.ctx.storage.get("revoke")) || [];
    if (list.length) await this.revoke(list.map((x) => x.u));
    await this.ctx.storage.delete("revoke");
  }

  async alarm() {
    const now = Date.now();
    const list = (await this.ctx.storage.get("revoke")) || [];
    const due = list.filter((x) => x.due <= now), rest = list.filter((x) => x.due > now);
    await this.revoke(due.map((x) => x.u));
    if (rest.length) {
      await this.ctx.storage.put("revoke", rest);
      await this.ctx.storage.setAlarm(Math.min(...rest.map((x) => x.due)));
    } else {
      await this.ctx.storage.delete("revoke");
    }
  }

  // ---- helpers

  /// Close joiner sockets that stopped pinging (crashed game, lost connection without a close).
  pruneStale() {
    const now = Date.now();
    for (const ws of this.joiners()) {
      let last = null;
      try { last = this.ctx.getWebSocketAutoResponseTimestamp(ws); } catch {}
      const a = ws.deserializeAttachment();
      const since = last ? last.getTime() : (a && a.at) || now;
      if (now - since > STALE_MS) {
        const host = this.host();
        if (host && a) send(host, { t: "peer-leave", peer: a.peer });
        if (a && a.turn && a.turn.length) this.ctx.waitUntil(this.revokeLater(a.turn));
        ws.serializeAttachment(null);
        try { ws.close(4408, "timed out"); } catch {}
      }
    }
  }

  host() {
    for (const ws of this.ctx.getWebSockets("host")) {
      const a = ws.deserializeAttachment();
      if (a && a.role === "host") return ws;
    }
    return null;
  }

  joiners() {
    return this.ctx.getWebSockets("join").filter((ws) => {
      const a = ws.deserializeAttachment();
      return a && a.role === "join";
    });
  }

  peerSocket(peer) {
    if (peer === HOST_PEER) return this.host();
    for (const ws of this.joiners()) if (ws.deserializeAttachment().peer === peer) return ws;
    return null;
  }

  async allocPeer() {
    let next = (await this.ctx.storage.get("nextPeer")) || 1;
    for (let i = 0; i < 65534; i++) {
      const p = next;
      next = next >= 65534 ? 1 : next + 1;
      if (!this.peerSocket(p)) { await this.ctx.storage.put("nextPeer", next); return p; }
    }
    return -1;
  }

  /// Fresh TURN login for one player. { servers: ICE list for the game, users: usernames to revoke later }
  async iceServers() {
    const stun = { servers: [{ urls: ["stun:stun.cloudflare.com:3478"] }], users: [] };
    const { TURN_KEY_ID, TURN_KEY_API_TOKEN } = this.env;
    if (!TURN_KEY_ID || !TURN_KEY_API_TOKEN) return stun;
    try {
      const r = await fetch(
        `https://rtc.live.cloudflare.com/v1/turn/keys/${TURN_KEY_ID}/credentials/generate-ice-servers`,
        {
          method: "POST",
          headers: { Authorization: `Bearer ${TURN_KEY_API_TOKEN}`, "Content-Type": "application/json" },
          body: JSON.stringify({ ttl: TURN_TTL_SECONDS }),
        }
      );
      if (!r.ok) return stun;
      const body = await r.json();
      const list = Array.isArray(body.iceServers) ? body.iceServers : [body.iceServers];
      // Drop port-53 URLs (often blocked / slow); keep 3478, 80, 443, 5349.
      const servers = list
        .map((s) => ({ ...s, urls: [].concat(s.urls).filter((u) => !/:53(\?|$)/.test(u)) }))
        .filter((s) => s.urls.length);
      const users = [...new Set(list.map((s) => s && s.username).filter(Boolean))];
      return { servers, users };
    } catch {
      return stun;
    }
  }

}

// ---------------------------------------------------------------- utils

function lobbyStub(env, code, locationHint) {
  const id = env.LOBBY.idFromName(code);
  return locationHint ? env.LOBBY.get(id, { locationHint }) : env.LOBBY.get(id);
}

// Create the lobby (and so the relay) in the host's region. Only matters when the object is first created.
function regionHint(req) {
  const cf = req.cf || {};
  switch (cf.continent) {
    case "OC": return "oc";
    case "AS": return "apac";
    case "EU": return "weur";
    case "AF": return "afr";
    case "SA": return "sam";
    case "NA": return Number(cf.longitude) < -100 ? "wnam" : "enam";
    default: return undefined;
  }
}

function internal(req, action, code) {
  const u = new URL(req.url);
  u.searchParams.set("_action", action);
  u.searchParams.set("_code", code);
  return new Request(u.toString(), { method: "GET", headers: req.headers });
}

function isWebSocket(req) {
  return (req.headers.get("Upgrade") || "").toLowerCase() === "websocket";
}

function randomCode() {
  const bytes = crypto.getRandomValues(new Uint8Array(CODE_LEN));
  let s = "";
  for (const b of bytes) s += ALPHABET[b % ALPHABET.length]; // 256 % 32 == 0, so no bias
  return s;
}

function normalizeCode(raw) {
  const c = String(raw || "").toUpperCase().replace(/[^A-Z0-9]/g, ""); // allows "abc-def" / spaces
  if (c.length !== CODE_LEN) return null;
  for (const ch of c) if (!ALPHABET.includes(ch)) return null;
  return c;
}

function cleanName(n) {
  const s = String(n || "Player").replace(/[\u0000-\u001f]/g, "").trim().slice(0, 32);
  return s || "Player";
}

function send(ws, obj) {
  try { ws.send(JSON.stringify(obj)); } catch {}
}

function json(obj, status = 200) {
  return new Response(JSON.stringify(obj), { status, headers: { "Content-Type": "application/json" } });
}
