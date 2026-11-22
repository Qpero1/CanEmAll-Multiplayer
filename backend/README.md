# Can 'Em All Multiplayer: lobby backend

This is a free Cloudflare Worker that gives the mod lobby codes, so players don't need port forwarding. Game traffic still goes peer-to-peer or through Cloudflare TURN. The Worker only handles matchmaking messages, plus a last-resort relay. See `PROTOCOL.md`.

## Deploy (one time)

```
cd backend
npm install
npx wrangler login
npx wrangler deploy
```

This prints your URL, e.g. `https://canemall-lobby.<you>.workers.dev`. Check it with `/v1/health`.

### TURN fallback (recommended)

1. Cloudflare dashboard → **Realtime → TURN Server → Create**. Copy the **Key ID** and **API token**.
2. Add them as secrets:
   ```
   npx wrangler secret put TURN_KEY_ID
   npx wrangler secret put TURN_KEY_API_TOKEN
   ```
3. `/v1/health` should now show `"turn": true`.

The first 1,000 GB per month of TURN traffic is free. Only players who are in a live lobby get credentials, and they expire after 6 h.

## Local testing

```
npm run dev          # wrangler dev on http://127.0.0.1:8787
npm test             # smoke test against it
```
