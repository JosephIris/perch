# Privacy

Perch runs on your machine and is built to keep your work there. It has **no
analytics, no telemetry, and no crash reporting** — it does not send usage data,
identifiers, or the contents of your terminals to the app's author or to any
third-party tracking service.

This document describes what Perch itself does over the network. It does not
cover the separate agent CLIs and shells you choose to run inside Perch's panes
(see "Tools you run inside Perch" below).

## What stays on your machine

These never leave your device by any action of Perch:

- Terminal output and scrollback buffers.
- Session state and layout (the SessionStore).
- Your settings.
- The local IPC between the app's WPF host and its WebView2 UI.

## Network connections Perch makes

Perch itself contacts exactly three endpoints, all first-party or public:

| Purpose | Endpoint | What is sent | When |
|---|---|---|---|
| **Update check + download** | `github.com` (this repo's Releases) | Nothing personal — a normal request for the public release feed | On the app's update checks |
| **Claude usage display** | `api.anthropic.com/api/oauth/usage` | Uses the Claude sign-in that Claude Code already manages, to read *your* usage/limits | Only when you're signed in to Claude |
| **Cloud price estimates** | `cloudbilling.googleapis.com` | Nothing personal — a request for Google Cloud's public price list | When computing cost estimates |

Notes:

- Perch does **not** create its own account or store its own copy of your
  provider credentials. The Anthropic usage call reuses the existing Claude
  Code sign-in; that data goes to Anthropic, your existing provider, not to
  Perch's author.
- The update check talks to GitHub the same way any download would. GitHub may
  log the request per its own privacy policy; Perch attaches no identity to it.

## Tools you run inside Perch

Perch runs other programs for you. When you run an agent CLI or shell command in a pane
(for example Claude Code, Codex, `gcloud`, or any other program), **that tool
does its own networking under your own accounts and credentials.** Perch
launches it and displays its output; it does not intercept, reroute, or
transmit that tool's data anywhere. What those tools send, and to whom, is
governed by *their* privacy policies, not this one.

## Changes

If Perch's network behavior changes, this document changes with it in the same
release. If you find a discrepancy between this document and what the app
actually does, please report it (see [`SECURITY.md`](SECURITY.md)).
