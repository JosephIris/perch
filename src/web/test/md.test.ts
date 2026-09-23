// The project chat's Markdown: the block and inline split Claude's replies
// are drawn from. Text only ever lands in text nodes, so what matters here is
// that each piece is recognised as the right kind — a missed fence would draw
// code as prose, a greedy bold would swallow a sentence.

import { test } from "node:test";
import assert from "node:assert/strict";
import { parseBlocks, parseInline } from "../src/md.js";

test("blocks: paragraphs, headings, lists, fences, quotes", () => {
  const b = parseBlocks([
    "## Plan",
    "First line",
    "second line",
    "",
    "- one",
    "- two",
    "  continued",
    "1. first",
    "2. second",
    "```",
    "code **not bold**",
    "```",
    "> quoted",
  ].join("\n"));
  assert.deepEqual(b.map((x) => x.kind), ["h", "p", "ul", "ol", "code", "quote"]);
  assert.deepEqual(b[1], { kind: "p", text: "First line\nsecond line" });
  assert.deepEqual(b[2], { kind: "ul", items: ["one", "two continued"] });
  assert.deepEqual(b[4], { kind: "code", text: "code **not bold**" });
});

test("a table is kept as monospace rows, without its divider", () => {
  const b = parseBlocks("| a | b |\n|---|---|\n| 1 | 2 |");
  assert.deepEqual(b, [{ kind: "code", text: "| a | b |\n| 1 | 2 |" }]);
});

test("inline: code, bold, italic, links (http only)", () => {
  const s = parseInline("Run `git log` then **merge** it, *carefully*, see [docs](https://x.dev/a) or [bad](javascript:alert(1)).");
  assert.deepEqual(s.map((x) => x.kind), ["text", "code", "text", "bold", "text", "italic", "text", "link", "text"]);
  assert.equal(s[7].kind === "link" && s[7].href, "https://x.dev/a");
  assert.ok(s[8].text.includes("[bad](javascript:alert(1))"));   // not a link
});

test("an unterminated fence runs to the end instead of vanishing", () => {
  const b = parseBlocks("text\n```\nline");
  assert.deepEqual(b, [{ kind: "p", text: "text" }, { kind: "code", text: "line" }]);
});
