// Unit tests for the per-pane header action buttons (split-right / split-down /
// open-browser) and the URL normalizer behind the browser prompt. The DOM
// builder is exercised against the same minimal shim style as pane-footer's
// tests; the message mapping and normalizeUrl are pure and tested directly.

import { test } from "node:test";
import assert from "node:assert/strict";
import { paneActionMessage, buildPaneActions } from "../src/pane-actions.js";
import { normalizeUrl } from "../src/browser-prompt.js";

// ---- action → message mapping (pure) --------------------------------------

test("split-right / split-down map to terminal pane.split (no url)", () => {
  assert.deepEqual(paneActionMessage("split-right", "p1"), {
    type: "pane.split", paneId: "p1", dir: "right",
  });
  assert.deepEqual(paneActionMessage("split-down", "p1"), {
    type: "pane.split", paneId: "p1", dir: "down",
  });
});

test("browser maps to a url-carrying split, or null without a url", () => {
  assert.deepEqual(paneActionMessage("browser", "p1", "https://x.com"), {
    type: "pane.split", paneId: "p1", dir: "right", url: "https://x.com",
  });
  // No url → null so the caller never opens a blank webview pane.
  assert.equal(paneActionMessage("browser", "p1"), null);
  assert.equal(paneActionMessage("browser", "p1", ""), null);
});

// ---- normalizeUrl (pure) ---------------------------------------------------

test("normalizeUrl passes real schemes through untouched", () => {
  assert.equal(normalizeUrl("https://example.com"), "https://example.com");
  assert.equal(normalizeUrl("http://localhost:3000/x"), "http://localhost:3000/x");
  assert.equal(normalizeUrl("file:///C:/x.html"), "file:///C:/x.html");
  assert.equal(normalizeUrl("about:blank"), "about:blank");
});

test("normalizeUrl adds http for localhost/loopback, https otherwise", () => {
  assert.equal(normalizeUrl("localhost:3000"), "http://localhost:3000");
  assert.equal(normalizeUrl("127.0.0.1:8080/app"), "http://127.0.0.1:8080/app");
  assert.equal(normalizeUrl("example.com"), "https://example.com");
  assert.equal(normalizeUrl("example.com:8443/path"), "https://example.com:8443/path");
  assert.equal(normalizeUrl("sub.example.com/a?b=c"), "https://sub.example.com/a?b=c");
});

test("normalizeUrl converts Windows drive + UNC paths to file URLs", () => {
  assert.equal(normalizeUrl("C:\\Users\\me\\report.html"), "file:///C:/Users/me/report.html");
  assert.equal(normalizeUrl("D:/build/index.html"), "file:///D:/build/index.html");
  assert.equal(normalizeUrl("\\\\nas\\share\\a.html"), "file://nas/share/a.html");
});

test("normalizeUrl rejects garbage (no dot/port, whitespace, empty)", () => {
  assert.equal(normalizeUrl(""), null);
  assert.equal(normalizeUrl("   "), null);
  assert.equal(normalizeUrl("just some words"), null);
  assert.equal(normalizeUrl("notaurl"), null);
});

test("normalizeUrl trims surrounding whitespace before parsing", () => {
  assert.equal(normalizeUrl("  example.com  "), "https://example.com");
});

// ---- DOM structure (minimal shim) -----------------------------------------

class El {
  tagName: string;
  className = "";
  children: El[] = [];
  dataset: Record<string, string> = {};
  attrs: Record<string, string> = {};
  draggable = true;
  title = "";
  type = "";
  constructor(tag: string) { this.tagName = tag.toUpperCase(); }
  setAttribute(k: string, v: string) { this.attrs[k] = v; }
  appendChild(n: El) { this.children.push(n); return n; }
  addEventListener() {}
  querySelectorAll(sel: string): El[] {
    const cls = sel.replace(/^\./, "");
    const out: El[] = [];
    const walk = (e: El) => { if (e.className.split(" ").includes(cls)) out.push(e); e.children.forEach(walk); };
    this.children.forEach(walk);
    return out;
  }
}

(globalThis as unknown as { document: unknown }).document = {
  createElement: (tag: string) => new El(tag),
  createElementNS: (_ns: string, tag: string) => new El(tag),
};

test("buildPaneActions renders three wired buttons in order", () => {
  const group = buildPaneActions("p1") as unknown as El;
  assert.equal(group.className, "pane__actions");
  const btns = group.querySelectorAll(".pane__action");
  assert.equal(btns.length, 3);
  assert.deepEqual(btns.map((b) => b.dataset.action), ["split-right", "split-down", "browser"]);
  // Each carries an accessible label and opts out of the header's HTML5 drag.
  for (const b of btns) {
    assert.equal(b.draggable, false);
    assert.ok(b.attrs["aria-label"] && b.attrs["aria-label"].length > 0);
    assert.equal(b.type, "button");
    // …and an SVG icon child.
    assert.equal(b.children.length, 1);
    assert.equal(b.children[0].tagName, "SVG");
  }
});
