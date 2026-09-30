// The terminal font setting never reaches xterm without the bundled fallback:
// a Windows font name on a Mac turned every pane proportional (v1.86.0).

import { test } from "node:test";
import assert from "node:assert/strict";
import { termFontStack, DEFAULT_TERM_FONT } from "../src/term-font.js";

test("no setting is the bundled chain", () => {
  assert.equal(termFontStack(undefined), DEFAULT_TERM_FONT);
  assert.equal(termFontStack("  "), DEFAULT_TERM_FONT);
});

test("a family name goes first, quoted, with the bundled chain behind it", () => {
  assert.equal(termFontStack("Cascadia Code"), `"Cascadia Code", ${DEFAULT_TERM_FONT}`);
});

test("a stack or a quoted name is kept as written, still with the chain behind it", () => {
  assert.equal(termFontStack("Menlo, monospace"), `Menlo, monospace, ${DEFAULT_TERM_FONT}`);
  assert.equal(termFontStack('"JetBrains Mono"'), `"JetBrains Mono", ${DEFAULT_TERM_FONT}`);
});
