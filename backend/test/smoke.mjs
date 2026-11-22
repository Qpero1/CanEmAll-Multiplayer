// Smoke test: node test/smoke.mjs [baseUrl]   (default http://127.0.0.1:8787)
import WebSocket from "ws";

const BASE = process.argv[2] || "http://127.0.0.1:8787";
const WS = BASE.replace(/^http/, "ws");
let failed = 0;
const ok = (c, m) => { console.log((c ? "PASS " : "FAIL ") + m); if (!c) failed++; };

function open(path) {
  const ws = new WebSocket(WS + path);
  const q = []; const waiters = [];
  ws.on("message", (d, bin) => {
    const s = bin ? null : d.toString(); if (s === "pong") return; const v = bin ? new Uint8Array(d) : JSON.parse(s);
    const w = waiters.shift(); if (w) w(v); else q.push(v);
  });
  ws.closeInfo = new Promise((r) => ws.on("close", (code) => r(code)));
  ws.next = (ms = 3000) => new Promise((res, rej) => {
    if (q.length) return res(q.shift());
    const t = setTimeout(() => rej(new Error("timeout")), ms);
    waiters.push((v) => { clearTimeout(t); res(v); });
  });
  ws.ready = new Promise((r, j) => { ws.on("open", r); ws.on("error", j); });
  return ws;
}
const sendJ = (ws, o) => ws.send(JSON.stringify(o));

const health = await (await fetch(BASE + "/v1/health")).json();
ok(health.ok && health.proto === 1, "health " + JSON.stringify(health));

// host
const host = open("/v1/host?ver=0.31.2&name=Arkin&max=3");
await host.ready;
const hosted = await host.next();
ok(hosted.t === "hosted" && /^[A-HJ-NP-Z2-9]{6}$/.test(hosted.code), "hosted code " + hosted.code);
ok(Array.isArray(hosted.ice) && hosted.ice.length >= 1, "ice servers present");
const code = hosted.code;

const info = await (await fetch(`${BASE}/v1/lobby/${code.toLowerCase().slice(0,3)}-${code.slice(3)}`)).json();
ok(info.exists && info.players === 1 && info.max === 3, "lobby info " + JSON.stringify(info));

// bad code
const bad = open("/v1/join/ZZZZZZ?ver=0.31.2&name=x"); await bad.ready;
const badMsg = await bad.next();
ok(badMsg.error === "not_found" && (await bad.closeInfo) === 4404, "unknown code rejected");

// version mismatch
const vm = open(`/v1/join/${code}?ver=0.30.0&name=x`); await vm.ready;
const vmMsg = await vm.next();
ok(vmMsg.error === "version" && (await vm.closeInfo) === 4409, "version mismatch: " + vmMsg.msg);

// joiner 1
const j1 = open(`/v1/join/${code}?ver=0.31.2&name=Friend`); await j1.ready;
const joined = await j1.next();
ok(joined.t === "joined" && joined.peer === 1 && joined.host === "Arkin", "joined as peer 1");
const pj = await host.next();
ok(pj.t === "peer-join" && pj.peer === 1 && pj.name === "Friend", "host told about peer-join");

// signal both ways
sendJ(j1, { t: "signal", to: 99, data: { ep: "1.2.3.4:5000" } });
const s1 = await host.next();
ok(s1.t === "signal" && s1.from === 1 && s1.data.ep === "1.2.3.4:5000", "joiner->host signal (forced to host)");
sendJ(host, { t: "signal", to: 1, data: { ep: "5.6.7.8:7777" } });
const s2 = await j1.next();
ok(s2.t === "signal" && s2.from === 0 && s2.data.ep === "5.6.7.8:7777", "host->joiner signal");

// relay binary: disabled on the server (RELAY_ENABLED = false) -> the joiner gets one clear error, the host nothing
j1.send(Uint8Array.from([0xff, 0xff, 9, 8, 7]));
const r1 = await j1.next();
ok(r1 && r1.t === "error" && r1.error === "relay_off", "relay disabled: joiner told why");
j1.send(Uint8Array.from([0xff, 0xff, 9, 8, 7]));
let leaked = false;
host.once("message", (d, bin) => { if (bin) leaked = true; });
await new Promise((r) => setTimeout(r, 300));
ok(!leaked, "relay disabled: nothing forwarded to the host");

// ping auto-response
j1.send("ping");
const pong = await new Promise((r) => j1.once("message", (d) => r(d.toString())));
ok(pong === "pong", "ping/pong keepalive");

// joiner 2 then full (max 3)
const j2 = open(`/v1/join/${code}?ver=0.31.2&name=Two`); await j2.ready;
ok((await j2.next()).peer === 2, "joined as peer 2"); await host.next();
const j3 = open(`/v1/join/${code}?ver=0.31.2&name=Three`); await j3.ready;
ok((await j3.next()).peer === 3, "server leaves 'full' to the host's game (joined as peer 3)"); await host.next();

// kick
sendJ(host, { t: "kick", peer: 2, reason: "bye" });
ok((await j2.next()).error === "kicked" && (await j2.closeInfo) === 4403, "kick works");
ok((await host.next()).t === "peer-leave", "host told about kicked peer leaving");

// joiner leaves
j1.close();
const pl = await host.next();
ok(pl.t === "peer-leave" && pl.peer === 1, "host told about peer-leave");

// host leaves -> lobby gone
const j4 = open(`/v1/join/${code}?ver=0.31.2&name=Late`); await j4.ready;
await j4.next(); await host.next();
host.close();
const closed = await j4.next();
ok(closed.t === "closed" && (await j4.closeInfo) === 4410, "joiners closed when host leaves");
await new Promise((r) => setTimeout(r, 300));
const gone = await fetch(`${BASE}/v1/lobby/${code}`);
ok(gone.status === 404, "lobby gone after host leaves");

console.log(failed ? `\n${failed} FAILED` : "\nALL PASSED");
process.exit(failed ? 1 : 0);
