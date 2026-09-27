// An email's plain body, as Gmail exports it, drawn like a mail client: a
// bracketed address becomes a link on its label or a picture, character codes
// decode, and nothing reads as a wall of URLs. Samples shaped like a real
// Jira notification (addresses made up).

import { test } from "node:test";
import assert from "node:assert/strict";
import { parseMailText, isImageUrl, decodeEntities, shortUrl } from "../src/mail-text.js";

test("a bracketed image becomes a picture with its label as description", () => {
  const segs = parseMailText("Jira logo\n[https://cdn.example.net/assets/jiraHeader.315f.png]\n\nHello");
  assert.deepEqual(segs[0], { kind: "image", src: "https://cdn.example.net/assets/jiraHeader.315f.png", alt: "Jira logo" });
  assert.deepEqual(segs[1], { kind: "text", text: "\n\nHello" });
});

test("a bracketed link takes the line above as its label, even when Gmail wrapped the address", () => {
  const segs = parseMailText("View work item\n[https://acme.atlassian.net/browse/PK-1?actionerId=55%3Aab-\ncd&x=1]\n");
  assert.deepEqual(segs, [{ kind: "link", text: "View work item", href: "https://acme.atlassian.net/browse/PK-1?actionerId=55%3Aab-cd&x=1" }]);
});

test("a link on the same line takes the words before it; a separator isn't part of a label", () => {
  const segs = parseMailText("Product Kanban [https://acme.atlassian.net/browse/PK] / PK-7256 [https://acme.atlassian.net/browse/PK-7256]");
  assert.deepEqual(segs.filter((s) => s.kind === "link").map((s) => s.kind === "link" && s.text), ["Product Kanban", "PK-7256"]);
});

test("bare addresses become short links; codes decode", () => {
  const segs = parseMailText("Assignee: Petr &#x2192; Joseph. See https://example.com/a/b?c=1.");
  assert.equal(segs[0].kind === "text" && segs[0].text, "Assignee: Petr → Joseph. See ");
  assert.deepEqual(segs[1], { kind: "link", text: "example.com/a/b", href: "https://example.com/a/b?c=1" });
  assert.equal(segs[2].kind === "text" && segs[2].text, ".");
});

test("a long paragraph is not swallowed as a label", () => {
  const para = "x".repeat(120);
  const segs = parseMailText(`${para} [https://example.com/p]`);
  assert.equal(segs[0].kind, "text");
  assert.deepEqual(segs[1], { kind: "link", text: "example.com/p", href: "https://example.com/p" });
});

test("a separator stays between two links; adjacent links keep a space", () => {
  const segs = parseMailText("Product Kanban\n[https://a.net/PK] / PK-7256\n[https://a.net/PK-7256]");
  assert.deepEqual(segs.map((s) => (s.kind === "text" ? s.text : s.kind === "link" ? `<${s.text}>` : "img")).join(""), "<Product Kanban> / <PK-7256>");
  const two = parseMailText("for Gmail\n[https://a.net/g] or Outlook\n[https://a.net/o]");
  assert.deepEqual(two.map((s) => (s.kind === "text" ? s.text : s.kind === "link" ? `<${s.text}>` : "img")).join(""), "<for Gmail> <or Outlook>");
});

test("a badge's tracking link makes the picture clickable instead of printing beside it", () => {
  const segs = parseMailText("Download on the App Store\n[https://cdn.a.net/appStore.png]https://track.a.net/t/935?m=x");
  assert.deepEqual(segs, [{ kind: "image", src: "https://cdn.a.net/appStore.png", alt: "Download on the App Store", href: "https://track.a.net/t/935?m=x" }]);
});

test("helpers", () => {
  assert.ok(isImageUrl("https://secure.gravatar.com/avatar/6005?d=x"));
  assert.ok(isImageUrl("https://x.net/a.PNG?v=2"));
  assert.ok(!isImageUrl("https://x.net/browse/PK-1"));
  assert.equal(decodeEntities("a &amp; b &nbsp;&lt;c&gt;"), "a & b  <c>");
  assert.equal(shortUrl("https://www.example.com/"), "example.com");
  assert.equal(parseMailText("a\n\n\n\n\nb")[0].kind === "text" && (parseMailText("a\n\n\n\n\nb")[0] as { text: string }).text, "a\n\nb");
});
