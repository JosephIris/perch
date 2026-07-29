// Browser-pane URL policy, page side.
//
// The regression this suite exists for: the page happily created a URL pane
// pointed at `file:///…/report.html` (from a terminal link, or a C:\ path typed
// into the browser prompt) while the host refused anything but http/https. The
// pane appeared, reported its rect, and then sat there as an empty dark
// rectangle — no WebView2, no error, indistinguishable from a page that never
// loaded. Both halves are covered here:
//
//   1. paneUrlKind matches the host's WebUrlPolicy.Classify case for case,
//      driven off the SHARED fixture both languages read.
//   2. normalizeUrl (the only way a user types a pane address) can never
//      produce a URL the pane won't display.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { paneUrlKind, canOpenInPane } from "../src/web-url.js";
import { normalizeUrl } from "../src/browser-prompt.js";

interface Case { url: string; kind: "web" | "html-file" | null }

const fixture = JSON.parse(
  readFileSync(join(process.cwd(), "test/fixtures/url-policy-cases.json"), "utf8")
) as { cases: Case[] };

// ---- shared truth table ----------------------------------------------------

test("every shared policy case classifies as the fixture says", () => {
  assert.ok(fixture.cases.length >= 25, "fixture shrank — cases are load-bearing");
  for (const c of fixture.cases) {
    assert.equal(
      paneUrlKind(c.url),
      c.kind,
      `${JSON.stringify(c.url)} should be ${c.kind}`
    );
  }
});

test("the fixture actually exercises both accept kinds and the reject path", () => {
  const kinds = new Set(fixture.cases.map((c) => c.kind));
  assert.ok(kinds.has("web"), "no web case");
  assert.ok(kinds.has("html-file"), "no html-file case");
  assert.ok(kinds.has(null), "no reject case");
});

// ---- the specific holes that produced blank panes --------------------------

test("a local .html report opens in a pane — the whole point of the file rule", () => {
  assert.equal(paneUrlKind("file:///C:/Users/me/design-loop/mockup.html"), "html-file");
  assert.ok(canOpenInPane("file:///C:/Users/me/design-loop/mockup.html"));
});

test("a file:// to anything but .html/.htm is refused, whatever the query looks like", () => {
  // The extension test must run on the PATH, not the raw string — otherwise
  // "setup.exe?x=.html" sneaks past.
  assert.equal(paneUrlKind("file:///C:/tools/setup.exe?ext=.html"), null);
  assert.equal(paneUrlKind("file:///C:/tools/setup.exe#.html"), null);
});

test("percent-escaped paths are decoded before the extension check", () => {
  assert.equal(paneUrlKind("file:///C:/out/my%20report.html"), "html-file");
  // %2E is ".", so this IS report.html once decoded.
  assert.equal(paneUrlKind("file:///C:/out/report%2Ehtml"), "html-file");
});

test("a malformed percent-escape is refused, not thrown", () => {
  assert.equal(paneUrlKind("file:///C:/out/bad%ZZ.html"), null);
});

test("script-ish and handler schemes never reach a pane", () => {
  for (const u of [
    "javascript:alert(1)",
    "data:text/html,<script>fetch('/etc/passwd')</script>",
    "vbscript:msgbox",
    "ms-settings:privacy",
    "about:blank",
  ]) {
    assert.equal(canOpenInPane(u), false, `${u} must be refused`);
  }
});

// ---- normalizeUrl can only emit pane-hostable URLs -------------------------

test("normalizeUrl output is ALWAYS something a pane can display", () => {
  const inputs = [
    "example.com", "localhost:3000", "127.0.0.1:8080", "https://x.dev/a?b=1",
    "http://x.dev", "C:\\Users\\me\\report.html", "C:/Users/me/report.html",
    "\\\\nas\\share\\report.html", "file:///C:/x.html",
    // things that must NOT produce a pane
    "about:blank", "ftp://ftp.gnu.org/", "mailto:a@b.c", "javascript:alert(1)",
    "C:\\tools\\setup.exe", "chrome://settings", "hello world", "notaurl", "",
  ];
  for (const raw of inputs) {
    const out = normalizeUrl(raw);
    if (out === null) continue;
    assert.ok(
      canOpenInPane(out),
      `normalizeUrl(${JSON.stringify(raw)}) → ${out}, which a pane can't display`
    );
  }
});

test("the addresses people actually type still resolve", () => {
  assert.equal(normalizeUrl("example.com"), "https://example.com");
  assert.equal(normalizeUrl("localhost:3000"), "http://localhost:3000");
  assert.equal(normalizeUrl("127.0.0.1:8080/app"), "http://127.0.0.1:8080/app");
  assert.equal(normalizeUrl("https://x.dev/a?b=1"), "https://x.dev/a?b=1");
  assert.equal(normalizeUrl("C:\\Users\\me\\report.html"), "file:///C:/Users/me/report.html");
  assert.equal(normalizeUrl("\\\\nas\\share\\r.html"), "file://nas/share/r.html");
});

test("addresses that would have opened a blank pane are refused at the field", () => {
  // Each of these used to pass normalizeUrl, create a pane, and paint nothing.
  for (const raw of [
    "about:blank",
    "ftp://ftp.gnu.org/",
    "mailto:hello@buildwithperch.com",
    "C:\\tools\\setup.exe",
    "C:\\Users\\me",
    "chrome://settings",
  ]) {
    assert.equal(normalizeUrl(raw), null, `${raw} should shake the field`);
  }
});

test("garbage is still garbage", () => {
  assert.equal(normalizeUrl(""), null);
  assert.equal(normalizeUrl("   "), null);
  assert.equal(normalizeUrl("hello world"), null);
  assert.equal(normalizeUrl("notaurl"), null);
});
