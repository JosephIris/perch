// Unit tests for the in-pane new-pane chooser (pane-chooser.ts). Runs under
// `node --test` after esbuild bundles this + the real source for node (see
// run-tests.mjs). Like the footer test, we stand up a tiny DOM shim — just
// enough of document/Element/event wiring for the chooser — and assert on the
// REAL shipped showPaneChooser: the labels it builds, the agent detection, and
// the choice each interaction resolves with.

import { test } from "node:test";
import assert from "node:assert/strict";
import { showPaneChooser, folderName, type ChooserChoice } from "../src/pane-chooser.js";

// ---- minimal DOM shim ------------------------------------------------------
// Enough of the DOM for showPaneChooser: element creation, class/attr/text,
// append/remove, addEventListener + a way to fire handlers, and querySelector
// by class. Mirrors the footer test's shim, extended for events + the button.

class TextNode {
  constructor(public text: string) {}
}

type Handler = (ev: any) => void;

class El {
  tagName: string;
  className = "";
  title = "";
  type = "";
  tabIndex = 0;
  childNodes: Array<El | TextNode> = [];
  parent: El | null = null;
  attrs: Record<string, string> = {};
  listeners: Record<string, Handler[]> = {};
  focused = false;

  constructor(tag: string) {
    this.tagName = tag.toUpperCase();
  }

  get classes(): string[] {
    return this.className.split(/\s+/).filter(Boolean);
  }
  classList = {
    add: (c: string) => {
      if (!this.classes.includes(c)) this.className = (this.className + " " + c).trim();
    },
    contains: (c: string) => this.classes.includes(c),
  };

  setAttribute(k: string, v: string) { this.attrs[k] = v; }
  appendChild(n: El | TextNode) {
    if (n instanceof El) n.parent = this;
    this.childNodes.push(n);
    return n;
  }
  append(...ns: Array<El | TextNode | string>) {
    for (const n of ns) this.appendChild(typeof n === "string" ? new TextNode(n) : n);
  }
  remove() {
    if (this.parent) this.parent.childNodes = this.parent.childNodes.filter((n) => n !== this);
    this.parent = null;
  }
  set textContent(v: string) { this.childNodes = [new TextNode(v)]; }
  get textContent(): string { return this.childNodes.map(textOf).join(""); }

  addEventListener(type: string, h: Handler) {
    (this.listeners[type] ??= []).push(h);
  }
  removeEventListener(type: string, h: Handler) {
    this.listeners[type] = (this.listeners[type] ?? []).filter((x) => x !== h);
  }
  fire(type: string, ev: any = {}) {
    for (const h of [...(this.listeners[type] ?? [])]) h(ev);
  }
  focus() { this.focused = true; }

  /** Depth-first search for the first descendant (or self) with class `cls`. */
  querySelector(sel: string): El | null {
    const cls = sel.startsWith(".") ? sel.slice(1) : sel;
    const stack: Array<El | TextNode> = [...this.childNodes];
    while (stack.length) {
      const n = stack.shift()!;
      if (n instanceof El) {
        if (n.classes.includes(cls)) return n;
        stack.push(...n.childNodes);
      }
    }
    return null;
  }

  /** All descendants with class `cls`, in document order. */
  queryAll(cls: string): El[] {
    const out: El[] = [];
    const stack: Array<El | TextNode> = [...this.childNodes];
    while (stack.length) {
      const n = stack.shift()!;
      if (n instanceof El) {
        if (n.classes.includes(cls)) out.push(n);
        stack.unshift(...n.childNodes);
      }
    }
    // unshift-DFS above visits children before later siblings; re-sort by a
    // simple pre-order index isn't needed — collect again breadth-stable:
    return out;
  }
}

function textOf(n: El | TextNode): string {
  return n instanceof TextNode ? n.text : n.textContent;
}

// HTMLButtonElement is referenced by an `instanceof` check in onKeyDown; make
// the shim's button elements pass it so the Enter branch behaves like a browser.
class HTMLButtonElementShim extends El {}
(globalThis as any).HTMLButtonElement = HTMLButtonElementShim;

(globalThis as unknown as { document: unknown }).document = {
  createElement: (tag: string) => (tag === "button" ? new HTMLButtonElementShim(tag) : new El(tag)),
};
// showPaneChooser focuses the primary option on the next frame; run it inline.
(globalThis as any).requestAnimationFrame = (cb: () => void) => { cb(); return 0; };

// ---- helpers ---------------------------------------------------------------

const CWD = "C:\\Users\\irisy\\dev-projects\\perch";
const DEFAULT_CWD = "C:\\Users\\irisy";

function open(over: Partial<{ cwd: string; agentType: string; defaultCwd: string }> = {}) {
  const pane = new El("div");
  const p = showPaneChooser(pane as unknown as HTMLElement, {
    cwd: over.cwd ?? CWD,
    agentType: over.agentType ?? "claude",
    defaultCwd: over.defaultCwd ?? DEFAULT_CWD,
  });
  const overlay = pane.querySelector(".pane-chooser")!;
  const opts = overlay.queryAll("pane-chooser__opt"); // [agent, same, default]
  return { pane, promise: p, overlay, opts };
}

function titleOf(optEl: El): string {
  return optEl.querySelector(".pane-chooser__opt-title")!.textContent;
}
function descOf(optEl: El): string {
  return optEl.querySelector(".pane-chooser__opt-desc")!.textContent;
}

// ---- folderName (pure) -----------------------------------------------------

test("folderName: last segment of a Windows path", () => {
  assert.equal(folderName("C:\\Users\\irisy\\dev-projects\\perch"), "perch");
});
test("folderName: trailing separators are ignored", () => {
  assert.equal(folderName("C:\\Users\\irisy\\dev-projects\\perch\\"), "perch");
});
test("folderName: unix path", () => {
  assert.equal(folderName("/home/irisy/code/perch"), "perch");
});
test("folderName: a drive root yields the drive", () => {
  assert.equal(folderName("C:\\"), "C:");
});
test("folderName: blank / all-separator falls back to the input", () => {
  assert.equal(folderName(""), "");
  assert.equal(folderName("/"), "/");
});

// ---- structure + labels ----------------------------------------------------

test("renders title, the cwd, and three options", () => {
  const { overlay, opts } = open();
  assert.equal(overlay.querySelector(".pane-chooser__title")!.textContent, "New pane");
  assert.equal(overlay.querySelector(".pane-chooser__sub")!.textContent, CWD);
  assert.equal(opts.length, 3);
  assert.equal(titleOf(opts[0]), "Start Claude Code here");
  assert.equal(titleOf(opts[1]), "Open a shell here");
  assert.equal(titleOf(opts[2]), "Open a shell in the default folder");
});

test("option descriptions show the folder names", () => {
  const { opts } = open();
  assert.equal(descOf(opts[0]), "perch");    // agent → source folder
  assert.equal(descOf(opts[1]), "perch");    // same  → source folder
  assert.equal(descOf(opts[2]), "irisy");    // default → default folder
});

test("the agent option is the primary one", () => {
  const { opts } = open();
  assert.equal(opts[0].classList.contains("pane-chooser__opt--primary"), true);
  assert.equal(opts[1].classList.contains("pane-chooser__opt--primary"), false);
});

test("agent label is Codex when the source pane ran Codex", () => {
  assert.equal(titleOf(open({ agentType: "codex" }).opts[0]), "Start Codex here");
});

test("agent label defaults to Claude Code when no agent ran", () => {
  assert.equal(titleOf(open({ agentType: "" }).opts[0]), "Start Claude Code here");
});

test("each option carries its number-key hint", () => {
  const { opts } = open();
  assert.equal(opts[0].querySelector(".pane-chooser__key")!.textContent, "1");
  assert.equal(opts[1].querySelector(".pane-chooser__key")!.textContent, "2");
  assert.equal(opts[2].querySelector(".pane-chooser__key")!.textContent, "3");
});

// ---- choices (clicks) ------------------------------------------------------

test("clicking an option resolves with its choice and begins teardown", async () => {
  const { overlay, promise, opts } = open();
  opts[1].fire("click");
  assert.equal(await promise, "same");
  // Removal is deferred (animationend / 260ms fallback); the closing class is
  // the synchronous signal that teardown began.
  assert.equal(overlay.classList.contains("pane-chooser--closing"), true);
});

test("clicking the agent option resolves 'agent'", async () => {
  const { promise, opts } = open();
  opts[0].fire("click");
  assert.equal(await promise, "agent");
});

test("clicking the default option resolves 'default'", async () => {
  const { promise, opts } = open();
  opts[2].fire("click");
  assert.equal(await promise, "default");
});

test("the Cancel button resolves 'cancel'", async () => {
  const { overlay, promise } = open();
  const cancel = overlay.queryAll("settings-btn")[0];
  cancel.fire("click");
  assert.equal(await promise, "cancel");
});

// ---- choices (keyboard) ----------------------------------------------------

const noop = { preventDefault() {}, stopPropagation() {} };

test("number keys pick the matching option", async () => {
  for (const [key, expected] of [["1", "agent"], ["2", "same"], ["3", "default"]] as const) {
    const { overlay, promise } = open();
    overlay.fire("keydown", { key, ...noop });
    assert.equal(await promise, expected as ChooserChoice);
  }
});

test("Escape resolves 'cancel'", async () => {
  const { overlay, promise } = open();
  overlay.fire("keydown", { key: "Escape", ...noop });
  assert.equal(await promise, "cancel");
});

test("only the first interaction counts (idempotent)", async () => {
  const { promise, opts } = open();
  opts[0].fire("click"); // agent
  opts[2].fire("click"); // ignored — already settled
  assert.equal(await promise, "agent");
});
