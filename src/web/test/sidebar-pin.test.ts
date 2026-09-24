// A project's tabs in the sidebar: its project chats sit at the top of the
// branch whatever their state, their running threads right under them, and
// everything else keeps the order the host gave it.

import { test } from "node:test";
import assert from "node:assert/strict";
import { pinProjectChats } from "../src/sidebar.js";
import type { SessionView } from "../src/bridge.js";

const s = (id: string, over: Partial<SessionView> = {}): SessionView => ({ id, title: id, ...over } as SessionView);

test("project chats go first, threads under them, the rest in host order", () => {
  const own = [
    s("a"),
    s("t1", { threadOf: "chat" }),
    s("b", { dormant: true }),
    s("chat", { isLead: true }),
    s("c"),
    s("t2", { threadOf: "chat", dormant: true }),
  ];
  const { live, idle } = pinProjectChats(own);
  assert.deepEqual(live.map((x) => x.id), ["chat", "t1", "a", "c"]);
  assert.deepEqual(idle.map((x) => x.id), ["b", "t2"]);
});

test("a slept project chat still sits at the top", () => {
  const { live, idle } = pinProjectChats([s("a"), s("chat", { isLead: true, dormant: true })]);
  assert.deepEqual(live.map((x) => x.id), ["chat", "a"]);
  assert.deepEqual(idle, []);
});

test("no project chat: unchanged", () => {
  const { live, idle } = pinProjectChats([s("a"), s("b", { dormant: true }), s("c")]);
  assert.deepEqual(live.map((x) => x.id), ["a", "c"]);
  assert.deepEqual(idle.map((x) => x.id), ["b"]);
});
