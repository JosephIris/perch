// The agent glyphs: the two pixel sprites and the path builder that draws
// them. The sprites are data copied from the marks themselves, so the tests
// pin the features that make each one recognisable — a regression here is a
// creature with no eyes, or a Codex blob with nothing cut out of it.

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  CLAUDE_SPRITE, CODEX_SPRITE, spriteFor, spritePath, spriteWidth, spriteHeight,
} from "../src/agent-glyph.js";

test("every sprite row is as wide as the first, and only pixels or gaps", () => {
  for (const s of [CLAUDE_SPRITE, CODEX_SPRITE]) {
    const w = spriteWidth(s);
    for (const row of s.rows) assert.equal(row.length, w, `${s.label}: ${row}`);
    for (const row of s.rows) assert.match(row, /^[#.]+$/, `${s.label}: ${row}`);
  }
});

test("the Claude Code creature is the banner's: 18 by 5 at 1x2 pixels, eyes at 5 and 12, four legs", () => {
  assert.equal(spriteWidth(CLAUDE_SPRITE), 18);
  assert.equal(CLAUDE_SPRITE.rows.length, 5);
  assert.equal(spriteHeight(CLAUDE_SPRITE), 10);
  const eyes = CLAUDE_SPRITE.rows[1];
  assert.equal(eyes[5], ".");
  assert.equal(eyes[12], ".");
  assert.equal(eyes[4], "#");
  assert.equal(eyes[13], "#");
  const legs = [...CLAUDE_SPRITE.rows[4]].map((c, i) => (c === "#" ? i : -1)).filter((i) => i >= 0);
  assert.deepEqual(legs, [4, 6, 11, 13]);
});

test("the Codex mark is 16 by 16 with the >_ cut out of the blob", () => {
  assert.equal(spriteWidth(CODEX_SPRITE), 16);
  assert.equal(CODEX_SPRITE.rows.length, 16);
  // The chevron: one hole per row from 5 to 10, walking right to its tip on
  // rows 7 and 8 and back.
  // (the first gap AFTER the body starts — rows 9 and 10 begin with a gap)
  const chevron = [5, 6, 7, 8, 9, 10].map((y) => {
    const row = CODEX_SPRITE.rows[y];
    return row.indexOf(".", row.indexOf("#"));
  });
  assert.deepEqual(chevron, [4, 5, 6, 6, 5, 4]);
  // The underscore: a 4-wide hole on row 10, right of the chevron.
  assert.equal(CODEX_SPRITE.rows[10].slice(8, 12), "....");
  assert.equal(CODEX_SPRITE.rows[10].slice(12), "####");
  // Solid body on both sides of the cut-outs, so they read as cut-outs.
  assert.equal(CODEX_SPRITE.rows[7].slice(0, 6), "######");
  assert.equal(CODEX_SPRITE.rows[7].slice(7), "#########");
});

test("spritePath merges each run of pixels into one rectangle", () => {
  assert.equal(spritePath(["##.#", "....", "#"]), "M0 0h2v1h-2zM3 0h1v1h-1zM0 2h1v1h-1z");
  assert.equal(spritePath([]), "");
});

test("only the two agents have a glyph", () => {
  assert.equal(spriteFor("claude"), CLAUDE_SPRITE);
  assert.equal(spriteFor("codex"), CODEX_SPRITE);
  assert.equal(spriteFor(""), undefined);
  assert.equal(spriteFor(undefined), undefined);
  assert.equal(spriteFor("gemini"), undefined);
});
