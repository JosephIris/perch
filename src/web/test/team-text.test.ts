// The room's message bodies are small documents: a bot posts a status report
// with bullets, a fenced snippet and now and then a markdown table. parseBlocks
// is the whole decision — what is a table, what is a list, what is just a
// sentence with a dash in it — so it carries the tests, and a tiny DOM shim
// covers what the renderer builds on top of it.
//
// The rules that cost the most if they regress:
//   - a table needs a real separator row under it; "a | b" in prose is prose
//   - a fence is opaque: pipes and hashes inside it are code
//   - `snake_case_column` names never turn into italics (bots write columns)
//   - a single newline is a hard break; a blank line starts a paragraph

import { test } from "node:test";
import assert from "node:assert/strict";

// ---- DOM shim (renderer tests only; the parser needs no DOM) ---------------

type Child = El | string;
class El {
  tag: string;
  className = "";
  children: Child[] = [];
  attrs: Record<string, string> = {};
  dataset: Record<string, string> = {};
  title = "";
  href = "";
  rel = "";
  classList = {
    add: (...cls: string[]) => { this.className = [...this.className.split(" ").filter(Boolean), ...cls].join(" "); },
  };
  constructor(tag: string) { this.tag = tag; }
  addEventListener() { /* links carry a click handler; nothing fires it here */ }
  appendChild<T extends El>(c: T): T { this.children.push(c); return c; }
  append(...cs: Child[]) { this.children.push(...cs); }
  setAttribute(k: string, v: string) { this.attrs[k] = v; }
  set textContent(v: string) { this.children = [v]; }
  get textContent(): string {
    return this.children.map((c) => (typeof c === "string" ? c : c.textContent)).join("");
  }
  /** Text with a marker where a <br> was, so hard breaks are assertable. */
  get flat(): string {
    return this.children.map((c) => (typeof c === "string" ? c : c.tag === "br" ? "\n" : c.flat)).join("");
  }
  all(tag: string): El[] {
    const out: El[] = [];
    for (const c of this.children) {
      if (typeof c === "string") continue;
      if (c.tag === tag) out.push(c);
      out.push(...c.all(tag));
    }
    return out;
  }
  withClass(cls: string): El[] {
    const out: El[] = [];
    for (const c of this.children) {
      if (typeof c === "string") continue;
      if (c.className.split(" ").includes(cls)) out.push(c);
      out.push(...c.withClass(cls));
    }
    return out;
  }
}
(globalThis as unknown as { document: unknown }).document = { createElement: (tag: string) => new El(tag) };

const { parseBlocks, appendBlocks } = await import("../src/text.js");
type Block = ReturnType<typeof parseBlocks>[number];

const kinds = (text: string): string[] => parseBlocks(text).map((b) => b.kind);
const only = <K extends Block["kind"]>(text: string, kind: K): Extract<Block, { kind: K }> => {
  const blocks = parseBlocks(text);
  assert.equal(blocks.length, 1, `expected one block, got ${blocks.map((b) => b.kind).join(", ")}`);
  assert.equal(blocks[0].kind, kind);
  return blocks[0] as Extract<Block, { kind: K }>;
};
function render(text: string, roster: string[] = []): { host: El; images: string[] } {
  const host = new El("div");
  const images = appendBlocks(host as unknown as HTMLElement, text, roster);
  return { host, images };
}

// ---- paragraphs ------------------------------------------------------------

test("plain prose is one paragraph, whatever its length", () => {
  const b = only("Both Galina tickets are live, assigned to malkhanova. Descriptions verified.", "para");
  assert.equal(b.lines.length, 1);
});

test("a single newline is a hard break; a blank line starts a new paragraph", () => {
  const blocks = parseBlocks("PK-7253 ETL prepared table\nPK-7254 counter validations\n\nThree questions sit at the bottom.");
  assert.deepEqual(blocks.map((b) => b.kind), ["para", "para"]);
  assert.deepEqual((blocks[0] as Extract<Block, { kind: "para" }>).lines.length, 2);
  const { host } = render("one\ntwo\n\nthree");
  assert.equal(host.all("p").length, 2);
  assert.equal(host.all("p")[0].flat, "one\ntwo");
  assert.equal(host.all("br").length, 1);
});

test("CRLF text parses the same as LF", () => {
  assert.deepEqual(kinds("a\r\n\r\n- one\r\n- two"), ["para", "list"]);
});

// ---- lists -----------------------------------------------------------------

test("bullets and numbers become lists; the marker style decides which", () => {
  assert.equal(only("- one\n- two", "list").ordered, false);
  assert.equal(only("* one\n* two", "list").ordered, false);
  assert.equal(only("• one\n• two", "list").ordered, false);
  assert.equal(only("1. one\n2. two", "list").ordered, true);
  assert.equal(only("1) one\n2) two", "list").ordered, true);
});

test("indentation nests one level under the item above", () => {
  const list = only("- counters\n  - today_\n  - last7days_\n- labels", "list");
  assert.deepEqual(list.items.map((i) => i.text), ["counters", "labels"]);
  assert.deepEqual(list.items[0].items.map((i) => i.text), ["today_", "last7days_"]);
  const { host } = render("- counters\n  - today_\n- labels");
  assert.equal(host.withClass("md-list").length, 2);          // outer + nested
  assert.equal(host.withClass("md-li").length, 3);
});

test("a wrapped line continues the item above rather than starting one", () => {
  const list = only("- the label is lossprice where losscode = 102\n    (lost to a higher bid)\n- pricing path", "list");
  assert.equal(list.items.length, 2);
  assert.match(list.items[0].text, /losscode = 102 \(lost to a higher bid\)$/);
});

test("numbers after bullets start a second list", () => {
  assert.deepEqual(kinds("- one\n1. two"), ["list", "list"]);
});

test("a lone dash, a dash inside a sentence, and a dash with no space are not a list", () => {
  assert.deepEqual(kinds("-"), ["para"]);
  assert.deepEqual(kinds("cost - benefit is the trade-off"), ["para"]);
  assert.deepEqual(kinds("-fixed vs non-fixed: which one is live"), ["para"]);
});

// ---- headings, quotes, rules ----------------------------------------------

test("headings keep their level, capped at four, and never swallow the hashes", () => {
  assert.equal(only("# Scope", "head").level, 1);
  assert.equal(only("#### Acceptance", "head").level, 4);
  assert.equal(only("###### deep", "head").level, 4);
  assert.equal(only("## Open questions ##", "head").text, "Open questions");
  assert.deepEqual(kinds("#nohash is a tag, not a heading"), ["para"]);
});

test("a quote run is one block and keeps its lines", () => {
  const q = only("> anton said this\n> and then this", "quote");
  assert.deepEqual(q.lines, ["anton said this", "and then this"]);
});

test("three dashes alone are a rule, and split the paragraphs around them", () => {
  assert.deepEqual(kinds("summary\n\n---\n\ndetails"), ["para", "rule", "para"]);
});

// ---- tables ----------------------------------------------------------------

test("a table with outer pipes: header, rows, and left alignment by default", () => {
  const t = only("| counter | populated | verdict |\n| --- | --- | --- |\n| today_ | 66% | use |\n| last7days_ | 12% | drop |", "table");
  assert.deepEqual(t.head, ["counter", "populated", "verdict"]);
  assert.deepEqual(t.align, ["left", "left", "left"]);
  assert.deepEqual(t.rows, [["today_", "66%", "use"], ["last7days_", "12%", "drop"]]);
});

test("a table without outer pipes parses the same", () => {
  const t = only("counter | populated\n--- | ---\ntoday_ | 66%", "table");
  assert.deepEqual(t.head, ["counter", "populated"]);
  assert.deepEqual(t.rows, [["today_", "66%"]]);
});

test("the separator row sets alignment per column", () => {
  const t = only("| a | b | c |\n|:---|---:|:---:|\n| 1 | 2 | 3 |", "table");
  assert.deepEqual(t.align, ["left", "right", "center"]);
});

test("ragged body rows are padded and over-long ones cut to the header", () => {
  const t = only("| a | b | c |\n| --- | --- | --- |\n| 1 | 2 |\n| 1 | 2 | 3 | 4 |", "table");
  assert.deepEqual(t.rows, [["1", "2", ""], ["1", "2", "3"]]);
});

test("an escaped pipe stays inside its cell", () => {
  const t = only("| tool | flag |\n| --- | --- |\n| grep | a \\| b |", "table");
  assert.deepEqual(t.rows, [["grep", "a | b"]]);
});

test("pipes in prose are prose: no separator row, no table", () => {
  assert.deepEqual(kinds("run a | b | c and see"), ["para"]);
  assert.deepEqual(kinds("counter | populated\nsomething else | here"), ["para"]);
  assert.deepEqual(kinds("| a | b |\n| - | - |"), ["para"]);          // one-dash cells: not a separator
  assert.deepEqual(kinds("| a | b |\n| --- |"), ["para"]);            // column count must match
});

test("the table ends at the first line without a pipe", () => {
  assert.deepEqual(kinds("| a |\n| --- |\n| 1 |\nand that is the summary"), ["table", "para"]);
});

test("a table renders as a real table inside a scrolling wrap", () => {
  const { host } = render("| counter | share |\n| --- | ---: |\n| today_ | 66% |");
  assert.equal(host.withClass("md-tablewrap").length, 1);
  assert.equal(host.all("table").length, 1);
  assert.deepEqual(host.all("th").map((c) => c.textContent), ["counter", "share"]);
  assert.deepEqual(host.all("td").map((c) => c.textContent), ["today_", "66%"]);
  assert.equal(host.all("th")[1].className, "md-th md-th--right");
});

// ---- fences ----------------------------------------------------------------

test("a fence is opaque: pipes inside it are code, not a table", () => {
  const blocks = parseBlocks("before\n\n```sql\nSELECT a | b\n--- | ---\n```\n\nafter");
  assert.deepEqual(blocks.map((b) => b.kind), ["para", "code", "para"]);
  const code = blocks[1] as Extract<Block, { kind: "code" }>;
  assert.equal(code.lang, "sql");
  assert.deepEqual(code.lines, ["SELECT a | b", "--- | ---"]);
});

test("an unclosed fence runs to the end rather than eating the rest as prose", () => {
  const code = only("```\nnpm test\nnpm run build", "code");
  assert.deepEqual(code.lines, ["npm test", "npm run build"]);
});

test("a fence renders as pre > code with the language on the box", () => {
  const { host } = render("```ts\nconst a = 1;\n```");
  const pre = host.withClass("md-pre")[0];
  assert.equal(pre.dataset.lang, "ts");
  assert.equal(pre.all("code")[0].textContent, "const a = 1;");
});

// ---- inline ----------------------------------------------------------------

test("bold, italic, strike, code and a labelled link render; column names do not", () => {
  const { host } = render("**scope** is *right*, ~~shaded~~ dropped, see `bid_event` and [PK-7253](https://x.atlassian.net/browse/PK-7253)");
  assert.equal(host.withClass("beat__strong")[0].textContent, "scope");
  assert.equal(host.withClass("md-em")[0].textContent, "right");
  assert.equal(host.withClass("md-del")[0].textContent, "shaded");
  assert.equal(host.withClass("beat__code")[0].textContent, "bid_event");
  const link = host.withClass("tf-link")[0];
  assert.equal(link.textContent, "PK-7253");
  assert.equal(link.href, "https://x.atlassian.net/browse/PK-7253");
});

test("underscores and loose asterisks stay literal — those are columns and arithmetic", () => {
  const { host } = render("lifetime_pbundle_avg_lossprice_fixed and 2 * 3 * 4");
  assert.equal(host.withClass("md-em").length, 0);
  assert.equal(host.all("p")[0].textContent, "lifetime_pbundle_avg_lossprice_fixed and 2 * 3 * 4");
});

test("a bare URL is still a link, and a mention still a chip, inside a bullet", () => {
  const { host } = render("- ping @ada at https://perch.local/docs/teams", ["ada"]);
  assert.equal(host.withClass("tf-link")[0].textContent, "https://perch.local/docs/teams");
  assert.equal(host.withClass("tf-mention")[0].textContent, "@ada");
});

// ---- images ----------------------------------------------------------------

test("an image path inside a list item is collected and chipped", () => {
  const { host, images } = render("- shot: C:\\dev\\perch\\design-loop\\room.png\n- and the rest");
  assert.deepEqual(images, ["C:\\dev\\perch\\design-loop\\room.png"]);
  assert.equal(host.withClass("tf-path")[0].textContent, "room.png");
});

test("image paths come back in order across blocks, backticked ones included", () => {
  const { images } = render("first /tmp/a.png\n\n- then `/tmp/b.png`\n\n| shot |\n| --- |\n| /tmp/c.png |");
  assert.deepEqual(images, ["/tmp/a.png", "/tmp/b.png", "/tmp/c.png"]);
});

// ---- the host's marker classes --------------------------------------------

test("the host is marked md, and md--rich only when it is more than paragraphs", () => {
  assert.equal(render("just prose\n\nand more prose").host.className, "md");
  assert.equal(render("- a bullet").host.className, "md md--rich");
});

test("an empty body renders nothing and claims no images", () => {
  const { host, images } = render("");
  assert.deepEqual(images, []);
  assert.equal(host.children.length, 0);
});
