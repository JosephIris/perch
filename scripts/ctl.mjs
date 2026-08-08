#!/usr/bin/env node
// Send one raw JSON verb to the running app's control pipe (test IPC).
//   node scripts/ctl.mjs '{"verb":"pty.send","text":"ls\r"}'
import net from "node:net";
import os from "node:os";
import path from "node:path";

const sock = path.join(os.tmpdir(), "CoreFxPipe_perch\\control");
const payload = process.argv[2];
if (!payload) { console.error("usage: ctl.mjs '<json>'"); process.exit(2); }
JSON.parse(payload); // fail fast on bad JSON
const c = net.connect(sock);
c.on("connect", () => c.end(payload + "\n", () => process.exit(0)));
c.on("error", (e) => { console.error(String(e)); process.exit(1); });
