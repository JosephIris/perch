// Unit tests for terminal link detection: web URLs plus local HTML-file
// references (file:// / drive / UNC / unix-absolute), the URL-vs-file overlap
// rule, and the file-token → file:// URL conversion. Pure functions, run under
// `node --test` after esbuild bundles them.

import { test } from "node:test";
import assert from "node:assert/strict";
import { findLinksInLine, htmlFileToUrl, HTML_FILE_RE, setHomeDir } from "../src/link-detect.js";

// ---- web URLs still detected ----------------------------------------------

test("a plain web URL is detected as a url link", () => {
  const links = findLinksInLine("see http://localhost:5173/ for the preview");
  assert.equal(links.length, 1);
  assert.equal(links[0].kind, "url");
  assert.equal(links[0].text, "http://localhost:5173/");
});

// ---- HTML file references --------------------------------------------------

test("a Windows drive path to an .html file is detected as a file link", () => {
  const line = "wrote report to C:\\Users\\me\\out\\report.html done";
  const links = findLinksInLine(line);
  assert.equal(links.length, 1);
  assert.equal(links[0].kind, "file");
  assert.equal(links[0].text, "C:\\Users\\me\\out\\report.html");
});

test("forward-slash drive, UNC, unix-absolute, and file:// forms all match", () => {
  assert.equal(findLinksInLine("open D:/build/index.html")[0]?.text, "D:/build/index.html");
  assert.equal(findLinksInLine("at \\\\nas\\share\\a.html now")[0]?.text, "\\\\nas\\share\\a.html");
  assert.equal(findLinksInLine("see /home/me/x.htm here")[0]?.text, "/home/me/x.htm");
  assert.equal(findLinksInLine("file:///C:/t/p.html")[0]?.text, "file:///C:/t/p.html");
});

test("a home-abbreviated ~\\ path (as Claude Code prints file recaps) is detected", () => {
  const line = "wrote ~\\AppData\\Local\\Temp\\report.html (40KB)";
  const links = findLinksInLine(line);
  assert.equal(links.length, 1);
  assert.equal(links[0].kind, "file");
  assert.equal(links[0].text, "~\\AppData\\Local\\Temp\\report.html");
});

test("htmlFileToUrl expands ~ against the pushed home dir", () => {
  setHomeDir("C:\\Users\\josep");
  assert.equal(
    htmlFileToUrl("~\\AppData\\Local\\Temp\\report.html"),
    "file:///C:/Users/josep/AppData/Local/Temp/report.html"
  );
  // No home dir known yet → left unresolved rather than mangled.
  setHomeDir("");
  assert.equal(htmlFileToUrl("~\\x\\a.html"), "file://~/x/a.html");
  setHomeDir("C:\\Users\\josep"); // restore for any later tests
});

test("a bare filename with no path separator is NOT detected (avoids prose noise)", () => {
  assert.equal(findLinksInLine("edit index.html and save").length, 0);
  assert.equal(findLinksInLine("the report.html file").length, 0);
});

test("a non-html path is not detected", () => {
  assert.equal(findLinksInLine("saved C:\\tmp\\data.json ok").length, 0);
});

// ---- URL / file overlap ----------------------------------------------------

test("an .html at the end of a web URL is one url link, not also a file", () => {
  const links = findLinksInLine("docs at https://site.com/guide/intro.html today");
  assert.equal(links.length, 1);
  assert.equal(links[0].kind, "url");
  assert.equal(links[0].text, "https://site.com/guide/intro.html");
});

test("a URL and a separate local file on one line are both detected, in order", () => {
  const line = "preview http://localhost:3000/ or open C:\\out\\report.html";
  const links = findLinksInLine(line);
  assert.equal(links.length, 2);
  assert.deepEqual(links.map((l) => l.kind), ["url", "file"]);
  assert.ok(links[0].start < links[1].start);
});

// ---- token → file:// URL ---------------------------------------------------

test("htmlFileToUrl converts each path shape to a navigable file URL", () => {
  assert.equal(htmlFileToUrl("C:\\Users\\me\\report.html"), "file:///C:/Users/me/report.html");
  assert.equal(htmlFileToUrl("D:/build/index.html"), "file:///D:/build/index.html");
  assert.equal(htmlFileToUrl("\\\\nas\\share\\a.html"), "file://nas/share/a.html");
  assert.equal(htmlFileToUrl("/home/me/x.html"), "file:///home/me/x.html");
  // already a URL → untouched
  assert.equal(htmlFileToUrl("file:///C:/x.html"), "file:///C:/x.html");
  assert.equal(htmlFileToUrl("https://x.com/a.html"), "https://x.com/a.html");
});

// ---- regex is anchored to .html/.htm --------------------------------------

test("HTML_FILE_RE only matches .html / .htm extensions", () => {
  const re = new RegExp(HTML_FILE_RE.source, "i");
  assert.ok(re.test("C:\\x\\a.html"));
  assert.ok(re.test("C:\\x\\a.htm"));
  assert.ok(!re.test("C:\\x\\a.txt"));
  assert.ok(!re.test("C:\\x\\a.htmlx")); // extension must end there
});
