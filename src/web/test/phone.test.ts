// The phone link's pairing link: what the QR code in Settings → Phone
// carries. The iPhone app parses exactly this (docs/PHONE-API.md), so a
// change here is a protocol change.

import { test } from "node:test";
import assert from "node:assert/strict";
import { pairingUrl } from "../src/phone.js";

test("the pairing link carries the name, port, token and every address, in order", () => {
  const url = pairingUrl({ name: "DESK PC", port: 47800, token: "abc-_123", hosts: ["192.168.1.5", "10.0.0.7"] });
  assert.ok(url.startsWith("perch://pair?"));
  const q = new URL(url.replace("perch://pair", "http://x/")).searchParams;
  assert.equal(q.get("v"), "1");
  assert.equal(q.get("name"), "DESK PC");
  assert.equal(q.get("port"), "47800");
  assert.equal(q.get("token"), "abc-_123");
  assert.deepEqual(q.get("hosts")!.split(","), ["192.168.1.5", "10.0.0.7"]);
});
