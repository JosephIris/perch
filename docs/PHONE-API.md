# Phone link API (v1)

The Perch iPhone app (`ios/`) talks to Perch on the same wifi. Perch's side is
`src/Perch.Core/PhoneServer.cs` (server) and `AppController.Phone.cs` (what a
tab looks like to the phone, where a line goes). The pairing QR code is drawn
by `src/web/src/phone.ts`. Change this file together with them.

## Turning it on

Settings → Phone → "Let your phone connect". Off by default. The first time it
is turned on Perch makes a pairing token and listens on port **47800**
(`Settings.PhonePort`). On Windows the firewall asks once; allow private
networks.

## Pairing

The QR code carries a link:

    perch://pair?v=1&name=DESK-PC&port=47800&token=<token>&hosts=192.168.1.5,10.0.0.7

- `hosts` is this computer's LAN addresses, best first. The phone tries them in
  order with `GET /v1/hello` and keeps the first that answers.
- `token` is the secret. The phone stores it (Keychain) and sends it on every
  request. "New code" in Settings replaces it, which unpairs every phone.
- `v` is the link format version.

The app registers the `perch` URL scheme, so the iPhone Camera app can open a
scanned code straight into it.

## Requests

Plain HTTP, JSON in and out, one request per connection. Every request needs

    Authorization: Bearer <token>

Without it (or with a wrong one) the answer is `401`. Ten wrong tokens from one
address within a minute and that address gets `429` for a minute, even with the
right token.

The token crosses the wifi unencrypted. That is acceptable on a home or office
network and not on public wifi; Settings says so. (TLS with a pinned
self-signed certificate is the upgrade path if this needs to go further.)

### `GET /v1/hello`

    { "app": "perch", "api": 1, "name": "DESK-PC" }

Use it to pick a working address and to check the pairing still holds.

### `GET /v1/sessions`

    { "sessions": [
        { "id": "5b0c…", "title": "fix login", "project": "perch",
          "kind": "claude", "state": "done",
          "canSend": true, "active": true, "asleep": false } ] }

- `kind`: `claude` (a Claude tab), `thread` (a project chat's thread),
  `chat` (a project chat), `codex`, `shell`.
- `state`: `idle`, `working`, `done` (finished, your move), `waiting` (asked
  you a question), `permission` (wants approval for a tool). The most urgent
  across the tab's panes.
- `canSend`: false for `codex` and `shell` in v1. Show them, don't offer the mic.
- `asleep`: the tab was put to sleep for being idle. Sending to it wakes it;
  the line goes in once its Claude is up (10–20 s).

The phone polls this every few seconds while it is open. A tab going from
`working` to `done` is the cue to fetch its answer.

### `POST /v1/sessions/{id}/send`

    { "text": "run the tests and push", "voice": true }

→ `{ "result": "queued" }`

- The line goes into the tab the same way Perch types everything into a
  Claude: when it is free, confirmed by Claude's own prompt hook, never
  retyped. Sent while Claude is working, it waits its turn.
- `voice: true` appends `[voice input]` so Claude reads for meaning rather
  than wording (the same tag the desktop's dictation adds).
- `result`: `queued`; `answered` (the tab was waiting on a question, which was
  dismissed and this line is the reply); `empty`; `unsupported` (codex or
  shell); a `404` with `missing` when the tab is gone.

### `GET /v1/sessions/{id}/reply`

    { "text": "All 42 tests pass. I pushed …", "atMs": 1790791219178 }

The latest answer: Claude's last prose block after the last prompt. `text` is
null while the turn is still on a tool call, or when there is no answer yet.
`atMs` is set for project chats and null for Claude tabs. The phone reads this
aloud with the system voice.

## Not in v1

- Anything when the phone is away from the wifi (needs a relay).
- Push notifications while the app is closed (needs a relay and APNs).
- Answering permission prompts, stopping a turn, codex and shell tabs.
- The live terminal.
