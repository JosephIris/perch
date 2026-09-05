// @mentions are the room's addressing. The chips the composer shows are
// derived from these functions, so a wrong parse addresses the wrong bot —
// these pin the boundary rules (start-of-text or whitespace, not inside an
// email), the roster match, and @everyone winning over per-bot mentions.

import { test } from "node:test";
import assert from "node:assert/strict";
import { parseMentions, mentionQueryAt, validateNickname, tokenizeMentions } from "../src/mention.js";

const ROSTER = ["Ada", "Bo", "Cy-2"];

test("a mention at the start and one after a space both resolve", () => {
  assert.deepEqual(parseMentions("@Ada please ask @bo", ROSTER).to, ["Ada", "Bo"]);
});

test("matching is case-insensitive and answers with the roster's spelling", () => {
  assert.deepEqual(parseMentions("@ADA @ada", ROSTER).to, ["Ada"]);
});

test("an email address is not a mention", () => {
  assert.equal(parseMentions("mail joseph@ada.com about it", ROSTER).to, null);
});

test("a name nobody on the team has stays literal", () => {
  assert.equal(parseMentions("@zed do it", ROSTER).to, null);
});

test("@everyone wins over per-bot mentions in the same post", () => {
  assert.equal(parseMentions("@Ada @everyone standup", ROSTER).to, "everyone");
  assert.equal(parseMentions("@all standup", ROSTER).to, "everyone");
});

test("trailing punctuation does not join the token", () => {
  assert.deepEqual(parseMentions("thanks @Ada, and @Bo.", ROSTER).to, ["Ada", "Bo"]);
  assert.deepEqual(parseMentions("(@Cy-2)", ROSTER).to, ["Cy-2"]);
});

test("the text is returned untouched", () => {
  const text = "@Ada look at **this**";
  assert.equal(parseMentions(text, ROSTER).text, text);
});

test("no mentions at all → null, so the host routes it", () => {
  assert.equal(parseMentions("who owns the sidebar?", ROSTER).to, null);
});

test("tokenize keeps literal runs and chips in order", () => {
  const toks = tokenizeMentions("hi @Ada and @nobody, @everyone", ROSTER);
  assert.deepEqual(toks, [
    { kind: "text", text: "hi " },
    { kind: "mention", text: "@Ada", nick: "Ada" },
    { kind: "text", text: " and @nobody, " },
    { kind: "everyone", text: "@everyone" },
  ]);
});

test("tokenize of plain text is one text token", () => {
  assert.deepEqual(tokenizeMentions("plain", ROSTER), [{ kind: "text", text: "plain" }]);
});

test("a bot nicknamed All is a mention, not the group", () => {
  const toks = tokenizeMentions("@all", ["All"]);
  assert.deepEqual(toks, [{ kind: "mention", text: "@all", nick: "All" }]);
});

test("query at the caret: the @ and the letters typed so far", () => {
  assert.deepEqual(mentionQueryAt("tell @Ad", 8), { start: 5, query: "Ad" });
  assert.deepEqual(mentionQueryAt("@", 1), { start: 0, query: "" });
});

test("query is null outside a mention token", () => {
  assert.equal(mentionQueryAt("tell Ada", 8), null);
  assert.equal(mentionQueryAt("a@b", 3), null);
  assert.equal(mentionQueryAt("@Ada done", 9), null);
});

test("query honours the caret, not the end of the text", () => {
  assert.deepEqual(mentionQueryAt("@Ada @Bo", 3), { start: 0, query: "Ad" });
});

test("nickname validation", () => {
  assert.equal(validateNickname("Ada", []), null);
  assert.equal(validateNickname("cy-2", []), null);
  assert.match(validateNickname("", []) ?? "", /nickname/i);
  assert.match(validateNickname("Ada Lovelace", []) ?? "", /letters, digits/);
  assert.match(validateNickname("-ada", []) ?? "", /starting with/);
  assert.match(validateNickname("a".repeat(25), []) ?? "", /up to 24/);
  assert.match(validateNickname("ada", ["Ada"]) ?? "", /already on the team/);
  assert.match(validateNickname("everyone", []) ?? "", /already uses/);
});
