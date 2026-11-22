# Lobby backend protocol (v1)

Base URL: `https://canemall-lobby.<your-subdomain>.workers.dev`

## HTTP

| Request | Response |
|---|---|
| `GET /v1/health` | `{ok, proto, turn}` (`turn` = TURN secrets configured) |
| `GET /v1/lobby/CODE` | `200 {exists:true, players, max, ver}` or `404 {exists:false}` |

Codes are 6 chars from `ABCDEFGHJKLMNPQRSTUVWXYZ23456789`; case, spaces and dashes are ignored.

## WebSocket

- Host: `wss://…/v1/host?ver=0.31.2&name=Arkin&max=8`
- Join: `wss://…/v1/join/CODE?ver=0.31.2&name=Friend`

Send the text `ping` about every 30 s. The server answers `pong` without waking the Durable Object.

Peer ids: the host is always `0`, and joiners are `1, 2, …`.

### Server → client (JSON text)

| `t` | Fields | When |
|---|---|---|
| `hosted` | `proto, code, peer:0, max, ice` | Host connected |
| `joined` | `proto, code, peer, host, ice` | Joiner accepted |
| `peer-join` | `peer, name` | (host) someone joined |
| `peer-leave` | `peer` | (host) a joiner left or was kicked |
| `signal` | `from, data` | Forwarded signal |
| `ice` | `ice` | Reply to an `ice` refresh |
| `error` | `error, msg` | Followed by a close (see below) |
| `closed` | `reason` | (joiner) host left, then close 4410 |

`ice` is Cloudflare's `iceServers` array: `[{urls:[…stun…]}, {urls:[turn:…, turns:…], username, credential}]`. If TURN secrets aren't set, only the STUN entry is sent. TURN credentials last 6 h; send `{"t":"ice"}` to refresh them.

Close codes: `4404` no such lobby · `4409` version mismatch · `4429` full · `4403` kicked · `4410` host left.

### Client → server (JSON text)

| `t` | Fields | Who |
|---|---|---|
| `signal` | `to, data` | Anyone. Joiners can only reach the host (`to` is ignored); the host picks the joiner. `data` is opaque. |
| `kick` | `peer, reason` | Host |
| `config` | `max` (2–8) | Host |
| `ice` | — | Anyone in a live lobby |

### Relay (binary frames, last resort)

`[u16 big-endian peer][payload]`, at most 2048 bytes in total.

- The host sets the target peer. A joiner's header is ignored and the frame always goes to the host.
- On delivery, the header is rewritten to the sender's peer id.

Every relayed frame counts against the free Worker request quota, so use the relay only when both direct UDP and TURN fail.

## Suggested connection order (client side)

1. Both sides send a STUN binding request from the LiteNetLib socket to get their public `ip:port`, then exchange it with `signal`.
2. Both sides punch (send to each other's endpoints). If a LiteNetLib connection comes up within about 3 s, the connection is direct.
3. Otherwise each side allocates on Cloudflare TURN (UDP 3478, then TCP/TLS 443) and exchanges relayed addresses with `signal`. LiteNetLib then runs over TURN.
4. Otherwise both sides fall back to the WebSocket binary relay.
