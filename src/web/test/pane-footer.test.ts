// Unit tests for the per-pane status footer (bottom bar) shipped in eb1cabc,
// plus the live-time formatters it relies on. Runs under `node --test` after
// esbuild bundles this + the sources for node (see package.json "test").
//
// applyPaneFooter is DOM-driven, so we stand up a tiny DOM shim — just enough
// of document/Element for the footer + elapsed helpers — and assert on the
// resulting text + structure. This tests the REAL shipped function, not a
// re-implementation.

import { test } from "node:test";
import assert from "node:assert/strict";
// Safe to import before the DOM shim is installed: these modules touch
// `document` only inside their functions (call-time), never at module init.
import { buildPaneFooter, applyPaneFooter } from "../src/pane-footer.js";
import { fmtElapsed, fmtAgo, fmtElapsedCoarse, fmtAgoCoarse } from "../src/elapsed.js";

// ---- minimal DOM shim ------------------------------------------------------

class TextNode {
  constructor(public text: string) {}
}

class El {
  tagName: string;
  className = "";
  childNodes: Array<El | TextNode> = [];
  dataset: Record<string, string> = {};
  classList: {
    toggle: (c: string, force?: boolean) => boolean;
    add: (c: string) => void;
    contains: (c: string) => boolean;
  };
  private _classes = new Set<string>();

  constructor(tag: string) {
    this.tagName = tag.toUpperCase();
    const set = this._classes;
    this.classList = {
      toggle: (c, force) => {
        const want = force === undefined ? !set.has(c) : force;
        if (want) set.add(c);
        else set.delete(c);
        return want;
      },
      add: (c) => void set.add(c),
      contains: (c) => set.has(c),
    };
  }

  appendChild(n: El | TextNode) {
    this.childNodes.push(n);
    return n;
  }
  append(...ns: Array<El | TextNode | string>) {
    for (const n of ns) this.childNodes.push(typeof n === "string" ? new TextNode(n) : n);
  }
  replaceChildren(...ns: Array<El | TextNode | string>) {
    this.childNodes = [];
    this.append(...ns);
  }
  set textContent(v: string) {
    this.childNodes = [new TextNode(v)];
  }
  get textContent(): string {
    return this.childNodes.map(textOf).join("");
  }
  get childElementCount(): number {
    return this.childNodes.filter((n) => n instanceof El).length;
  }
}

function textOf(n: El | TextNode): string {
  return n instanceof TextNode ? n.text : n.textContent;
}

(globalThis as unknown as { document: unknown }).document = {
  createElement: (tag: string) => new El(tag),
};

// ---- helpers ---------------------------------------------------------------

type Leaf = Parameters<typeof applyPaneFooter>[1];

function leaf(over: Partial<Leaf> = {}): Leaf {
  return {
    kind: "leaf",
    paneId: "p1",
    name: "pane",
    colorIndex: 0,
    agentState: "idle",
    activityDetail: "",
    branch: "",
    ports: [],
    notification: null,
    commitCount: 0,
    linesAdded: 0,
    linesDeleted: 0,
    filesChanged: 0,
    ahead: 0,
    turnStartMs: 0,
    doneAtMs: 0,
    ...over,
  } as Leaf;
}

function render(l: Leaf, active: boolean) {
  const f = buildPaneFooter();
  applyPaneFooter(f, l, active);
  return f as unknown as { root: El; activityEl: El; metaEl: El };
}

// ---- activity (left cluster) ----------------------------------------------

test("working shows the activity verb + a live elapsed span", () => {
  const f = render(leaf({ agentState: "working", activityDetail: "editing live-updates.ts", turnStartMs: 1000 }), true);
  assert.match(f.activityEl.textContent, /^▸ editing live-updates\.ts · /);
  // the elapsed span carries the turn-start for the shared ticker
  const span = f.activityEl.childNodes.find((n: any) => n.dataset?.turnStart) as any;
  assert.equal(span?.dataset.turnStart, "1000");
});

test("working with no detail falls back to 'working'", () => {
  const f = render(leaf({ agentState: "working", turnStartMs: 0 }), true);
  assert.equal(f.activityEl.textContent, "▸ working");
});

test("working with no turn start omits the elapsed span", () => {
  const f = render(leaf({ agentState: "working", activityDetail: "x", turnStartMs: 0 }), true);
  assert.equal(f.activityEl.textContent, "▸ x");
  assert.equal(f.activityEl.childNodes.some((n: any) => n.dataset?.turnStart), false);
});

test("done shows finished + a live 'ago' span when stamped", () => {
  const f = render(leaf({ agentState: "done", doneAtMs: 5000 }), true);
  assert.match(f.activityEl.textContent, /^✓ finished · /);
  const span = f.activityEl.childNodes.find((n: any) => n.dataset?.since) as any;
  assert.equal(span?.dataset.since, "5000");
});

test("waiting / permission show their words", () => {
  assert.equal(render(leaf({ agentState: "waiting" }), true).activityEl.textContent, "waiting for you");
  assert.equal(render(leaf({ agentState: "permission" }), true).activityEl.textContent, "needs permission");
});

test("the activity carries the state as a data attribute", () => {
  assert.equal(render(leaf({ agentState: "done", doneAtMs: 1 }), true).activityEl.dataset.state, "done");
});

// ---- meta (right cluster) + active gating ---------------------------------

test("git diff/ahead chips show only on the ACTIVE pane (repo-wide stats)", () => {
  const l = leaf({ agentState: "done", doneAtMs: 1, linesAdded: 142, linesDeleted: 38, filesChanged: 7, ahead: 2 });

  const activeMeta = render(l, true).metaEl.textContent;
  assert.match(activeMeta, /\+142/);
  assert.match(activeMeta, /−38/);
  assert.match(activeMeta, /7 files/);
  assert.match(activeMeta, /↑2/);

  // Same leaf, inactive → git stats hidden (a split sibling shares HEAD).
  const inactiveMeta = render(l, false).metaEl.textContent;
  assert.doesNotMatch(inactiveMeta, /\+142/);
  assert.doesNotMatch(inactiveMeta, /↑2/);
});

test("files-changed chip is singular for one file", () => {
  const f = render(leaf({ agentState: "done", doneAtMs: 1, linesAdded: 1, filesChanged: 1 }), true);
  assert.match(f.metaEl.textContent, /1 file(?!s)/);
});

test("ports show regardless of active state (per-pane, not repo-wide)", () => {
  assert.match(render(leaf({ ports: [5173] }), false).metaEl.textContent, /:5173/);
  assert.match(render(leaf({ ports: [3000] }), true).metaEl.textContent, /:3000/);
});

// ---- empty collapse --------------------------------------------------------

test("an idle shell with nothing to say collapses the footer", () => {
  const f = render(leaf({ agentState: "idle" }), true);
  assert.equal(f.root.classList.contains("pane__footer--empty"), true);
});

test("a port alone keeps the footer alive even when idle", () => {
  const f = render(leaf({ agentState: "idle", ports: [3000] }), false);
  assert.equal(f.root.classList.contains("pane__footer--empty"), false);
});

test("idle but active with a diff still collapses (diff hidden, nothing else)", () => {
  // diff is active-gated; an idle ACTIVE pane with only a diff shows the diff,
  // so it is NOT empty — guard the inverse: inactive idle with only a diff IS.
  const f = render(leaf({ agentState: "idle", linesAdded: 5 }), false);
  assert.equal(f.root.classList.contains("pane__footer--empty"), true);
});

// ---- formatters ------------------------------------------------------------

test("fmtElapsed: seconds → minutes → hours, clamps negatives", () => {
  assert.equal(fmtElapsed(-100), "0s");
  assert.equal(fmtElapsed(0), "0s");
  assert.equal(fmtElapsed(8_000), "8s");
  assert.equal(fmtElapsed(65_000), "1m");
  assert.equal(fmtElapsed(3_660_000), "1h 1m");
});

test("fmtAgo: 'now' under 5s, then s/m/h/d, clamps negatives", () => {
  assert.equal(fmtAgo(-100), "now");
  assert.equal(fmtAgo(2_000), "now");
  assert.equal(fmtAgo(8_000), "8s ago");
  assert.equal(fmtAgo(120_000), "2m ago");
  assert.equal(fmtAgo(3_600_000), "1h ago");
  assert.equal(fmtAgo(90_000_000), "1d ago");
});

test("coarse formatters drop seconds (footer uses these)", () => {
  // under a minute → no seconds counter
  assert.equal(fmtElapsedCoarse(8_000), "<1m");
  assert.equal(fmtElapsedCoarse(0), "<1m");
  assert.equal(fmtElapsedCoarse(65_000), "1m");
  assert.equal(fmtElapsedCoarse(3_660_000), "1h 1m");
  assert.equal(fmtAgoCoarse(8_000), "just now");
  assert.equal(fmtAgoCoarse(120_000), "2m ago");
  assert.equal(fmtAgoCoarse(3_600_000), "1h ago");
});

test("the footer's working elapsed span is coarse (no per-second ticking)", () => {
  const f = render(leaf({ agentState: "working", activityDetail: "x", turnStartMs: Date.now() - 8_000 }), true);
  const span = f.activityEl.childNodes.find((n: any) => n.dataset?.turnStart) as any;
  assert.equal(span?.dataset.coarse, "1");
  assert.equal(span?.textContent, "<1m"); // 8s elapsed shows "<1m", not "8s"
});
