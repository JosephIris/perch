// "Open in a tab" builds a standalone document out of an artefact.
//
// The tab is a separate WebView2 with no access to the app's bundle, so the
// two things that can go wrong are silent: styling that never travels with the
// file, and an artefact title going into the page as markup instead of text.
// Both are covered here.

import { test } from "node:test";
import assert from "node:assert/strict";

// ---- Minimal `document.styleSheets` shim ----------------------------------
// documentCss() lifts rules out of the LIVE stylesheet so the exported page
// can't drift from the room. The shim stands in for that stylesheet, including
// one sheet that throws on access (a cross-origin sheet must be skipped, not
// crash the export) and one @font-face-ish rule with no selector.
function stubStyleSheets(sheets: unknown[]) {
  (globalThis as any).document = { styleSheets: sheets };
}

const rule = (selectorText: string, cssText: string) => ({ selectorText, cssText });

test.beforeEach(() => {
  stubStyleSheets([
    {
      cssRules: [
        rule(":root", ":root { --color-text-primary: #fff; }"),
        rule(".md-p", ".md-p { margin: 0; }"),
        rule(".md-h.md-h--1", ".md-h--1 { font-weight: 600; }"),
        rule(".team-arte__pre", ".team-arte__pre { white-space: pre; }"),
        rule(".sidebar__row", ".sidebar__row { display: flex; }"),
        { cssText: "@font-face { font-family: Inter; }" },   // no selectorText
      ],
    },
    { get cssRules(): never { throw new Error("cross-origin"); } },
  ]);
});

test("the document carries the styles its markup needs, and nothing else", async () => {
  const { artefactDocument } = await import("../src/artefact-export.js");
  const html = artefactDocument("Draft ticket", "markdown · from Bo", "<div class=\"md\"><p class=\"md-p\">hi</p></div>");

  // Tokens first — every rule below refers to them.
  assert.match(html, /--color-text-primary/);
  // The markdown renderer's own classes.
  assert.match(html, /\.md-p \{/);
  assert.match(html, /\.md-h--1/);
  assert.match(html, /\.team-arte__pre/);
  // Not the whole app: chrome the document can't contain stays out.
  assert.ok(!html.includes(".sidebar__row"), "app chrome leaked into the export");
  // A stylesheet we may not read must be skipped, not fatal — we got here.
  assert.match(html, /<!doctype html>/i);
});

test("title and meta are text, never markup", async () => {
  const { artefactDocument } = await import("../src/artefact-export.js");
  // A bot names an artefact after a bug it found. Nothing in that name may
  // execute in the tab we open.
  const html = artefactDocument(
    "<img src=x onerror=alert(1)> & \"quoted\"",
    "markdown · from <script>Bo</script>",
    "<p class=\"md-p\">body</p>",
  );
  assert.ok(!html.includes("<img src=x"), "title was injected as markup");
  assert.ok(!html.includes("<script>Bo"), "meta was injected as markup");
  assert.match(html, /&lt;img src=x/);
  assert.match(html, /&amp;/);
  // The body IS ours (built by the room's own renderer), so it stays markup.
  assert.match(html, /<p class="md-p">body<\/p>/);
});

test("the title appears in the tab's <title>, which is what names the tab", async () => {
  const { artefactDocument } = await import("../src/artefact-export.js");
  const html = artefactDocument("Sprint plan", "markdown · from Ada", "<p>x</p>");
  assert.match(html, /<title>Sprint plan<\/title>/);
});
