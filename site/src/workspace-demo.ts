// The interactive hero: an automated, looping demo of perch actually working.
// A cursor clicks in, a prompt types out, a Claude Code pane streams its work,
// a plain PowerShell console live-reloads underneath, and the inspector fills
// in with the file changes and before/after previews. Built from the app's
// REAL components (Sidebar, pane headers) plus the hand-built inspector.
//
// Kept deliberately small: sidebar, one agent pane, one console pane, the
// inspector. Everything auto-scrolls so nothing spills out of bounds.

import { Sidebar } from "../../src/web/src/sidebar.js";
import {
  buildPaneHeader, applyAgentBadge, applyChips, applyPorts, applyModelChip,
} from "../../src/web/src/pane-header.js";
import {
  buildInspectorShell, rowEl, bannerBefore, bannerAfter, CHANGES,
} from "./inspector-demo.js";

const reduce = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false;
const wait = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

const h = (tag: string, cls = "", html = ""): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (html) e.innerHTML = html;
  return e;
};

/** A terminal pane: real pane header (fixtured) + a scrolling body. */
function pane(opts: {
  color: number; name: string; agent?: string; model?: string;
  branch?: string; ports?: number[];
}): { el: HTMLElement; body: HTMLElement; header: any; leaf: any } {
  const el = h("div", "wspane pane");
  el.dataset.color = String(opts.color);
  const leaf: any = {
    kind: "leaf", paneId: opts.name, colorIndex: opts.color,
    branch: opts.branch ?? "", commitCount: 0, ports: opts.ports ?? [],
    agentType: opts.agent ?? "", model: opts.model ?? "",
  };
  const hd = buildPaneHeader(opts.name);
  hd.colorDotEl.dataset.color = String(opts.color);
  hd.nameEl.textContent = opts.name;
  applyAgentBadge(hd.agentBadgeEl, opts.agent);
  applyModelChip(hd.modelEl as HTMLButtonElement, opts.agent, opts.model);
  applyChips(hd.branchEl, hd.commitsEl, leaf, true);
  applyPorts(hd.portsEl as HTMLButtonElement, leaf);
  hd.stateDotEl.dataset.state = "idle";
  el.appendChild(hd.root);
  const body = h("div", "wspane__body");
  body.appendChild(h("div", "wst"));
  el.appendChild(body);
  return { el, body, header: hd, leaf };
}

const SPIN = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

export function buildWorkspaceDemo(sessions: any[], projects: any[]): HTMLElement {
  const win = h("div", "wsdemo");
  win.appendChild(h("div", "wsdemo__titlebar", `
    <span class="wsdemo__brand"><span class="wsdemo__glyph"></span>perch</span>
    <span class="wsdemo__winbtns"><i></i><i></i><i class="wsdemo__x"></i></span>
  `));

  const bodyGrid = h("div", "wsdemo__body");

  // Sidebar (real component). We keep our own reference to the active session
  // so we can flip it working -> done during the loop.
  const active = sessions.find((s) => s.id === "holiday-banner");
  const side = h("aside", "wsdemo__sidebar sidebar");
  side.appendChild(h("div", "wsside__top", `
    <div class="wsside__seg"><button class="wsside__seg-btn">Sessions</button><button class="wsside__seg-btn wsside__seg-btn--on">Projects</button></div>
    <div class="wsside__action"><span class="wsside__plus">+</span> New session</div>
    <div class="wsside__action"><span class="wsside__grid"></span> Dashboard <span class="wsside__badge">3</span></div>
  `));
  const scroll = h("div", "sidebar__scroll");
  const listEl = h("div");
  scroll.appendChild(listEl);
  side.appendChild(scroll);
  const sb = new Sidebar(listEl, h("button"), h("div"));
  const renderSidebar = () => sb.render(sessions, "holiday-banner", [], projects, "projects");

  // Workspace: agent pane (top) + console pane (bottom).
  const work = h("div", "wsdemo__workspace");
  const agent = pane({ color: 0, name: "holiday banner", agent: "claude", model: "opus", branch: "main" });
  agent.el.classList.add("wspane--agent");
  const console_ = pane({ color: 3, name: "storefront-web", branch: "main", ports: [5173] });
  console_.el.classList.add("wspane--console");
  work.append(agent.el, console_.el);

  // Inspector (fills in over time).
  const insp = buildInspectorShell();
  const inspCol = h("div", "wsdemo__inspector");
  inspCol.appendChild(insp.rail);

  bodyGrid.append(side, work, inspCol);
  win.appendChild(bodyGrid);

  // Animated cursor.
  const cursor = h("div", "wscursor");
  cursor.innerHTML = `<svg viewBox="0 0 24 24" width="22" height="22"><path d="M4 3 L4 19 L8.5 14.5 L11.5 21 L14 20 L11 13.5 L17.5 13.5 Z" fill="#fff" stroke="#0b0b0b" stroke-width="1.2" stroke-linejoin="round"/></svg>`;
  win.appendChild(cursor);

  const agentWst = agent.body.querySelector<HTMLElement>(".wst")!;
  const consoleWst = console_.body.querySelector<HTMLElement>(".wst")!;

  const scrollDown = (elm: HTMLElement) => { elm.scrollTop = elm.scrollHeight; };
  const line = (host: HTMLElement, box: HTMLElement, html: string) => {
    box.appendChild(h("div", "wst__l", html));
    scrollDown(host);
  };
  const agentLine = (html: string) => line(agent.body, agentWst, html);
  const consoleLine = (html: string) => line(console_.body, consoleWst, html);
  const inspRow = (r: any) => { insp.stream.appendChild(rowEl(r)); scrollDown(insp.stream); };

  const setState = (state: string, label = "") => {
    agent.header.stateDotEl.dataset.state = state;
    agent.header.stateLabelEl.textContent = label;
  };

  // Cursor motion (positions relative to the window box).
  async function cursorTo(target: HTMLElement, ms = 620): Promise<void> {
    const r = target.getBoundingClientRect();
    const c = win.getBoundingClientRect();
    cursor.style.opacity = "1";
    cursor.style.left = `${r.left - c.left + r.width / 2}px`;
    cursor.style.top = `${r.top - c.top + r.height / 2}px`;
    await wait(ms);
  }
  async function cursorClick(): Promise<void> {
    cursor.classList.add("wscursor--down");
    const r = h("span", "wscursor__ring");
    r.style.left = cursor.style.left;
    r.style.top = cursor.style.top;
    win.appendChild(r);
    await wait(150);
    cursor.classList.remove("wscursor--down");
    setTimeout(() => r.remove(), 500);
    await wait(150);
  }

  async function typePrompt(text: string): Promise<HTMLElement> {
    const l = h("div", "wst__l");
    const caret = h("span", "wst-user", "> ");
    const txt = h("span", "wst-user");
    l.append(caret, txt);
    agentWst.appendChild(l);
    for (const ch of text) { txt.textContent += ch; scrollDown(agent.body); await wait(26); }
    return l;
  }

  function resetAll() {
    agentWst.replaceChildren();
    consoleWst.replaceChildren();
    insp.stream.replaceChildren();
    insp.setChanges([], false);
    setState("idle", "");
    applyChips(agent.header.branchEl, agent.header.commitsEl, { ...agent.leaf, commitCount: 0 }, true);
    active.agentState = "working";
    active.turnStartMs = Date.now();
    active.doneAtMs = 0;
    active.ahead = 3; active.aheadMine = 2; active.linesAdded = 0; active.linesDeleted = 0;
    renderSidebar();
    // console starts with the dev server already up
    consoleLine(`<span class="wst__sub">PS C:\\dev\\storefront-web&gt;</span> npm run dev`);
    consoleLine(`&nbsp;`);
    consoleLine(`<span class="wst-vite">VITE v6.0.3</span> <span class="wst__sub">ready in</span> 241 ms`);
    consoleLine(`<span class="wst-arrow">→</span> <span class="wst__sub">Local:</span>   <span class="wst-link">http://localhost:5173/</span>`);
    cursor.style.left = "82%"; cursor.style.top = "96%"; cursor.style.opacity = "0";
  }

  // Static end-state for reduced motion (no cursor, no timers): the finished
  // transcript, the full journal, a done sidebar.
  function renderFinal() {
    resetAll();
    agentLine(`<span class="wst-user">&gt; make the holiday banner match the shop, and show me before and after</span>`);
    agentLine(`<span class="wst-b">●</span> I'll capture the current banner first, then restyle it against the shop tokens.`);
    agentLine(`<span class="wst-b">●</span> <b>Bash</b>(node scripts/capture.mjs --route /?banner=holiday)`);
    agentLine(`<span class="wst__sub">&nbsp;&nbsp;└ captured design/banner-before.png</span>`);
    agentLine(`<span class="wst-b">●</span> <b>Update</b>(src/banner.css)`);
    agentLine(`<span class="wst-b">●</span> The gradient is gone and the headline now uses the shop serif.`);
    consoleLine(`<span class="wst__sub">14:32:07</span> <span class="wst-vite">[vite]</span> hmr update banner.css`);
    inspRow({ kind: "prompt", text: "make the holiday banner match the rest of the shop, and show me before and after" });
    inspRow({ kind: "beat", time: "00:15", text: "I'll capture the current banner first, then restyle it against the shop tokens." });
    inspRow({ kind: "work", time: "00:15", verb: "Bash", target: "scripts/capture.mjs", note: "banner-before.png" });
    inspRow({ kind: "image", time: "00:15", svg: bannerBefore() });
    inspRow({ kind: "work", time: "00:16", verb: "Update", target: "src/banner.css", repeat: 2 });
    inspRow({ kind: "image", time: "00:18", svg: bannerAfter() });
    inspRow({ kind: "beat", time: "00:19", text: "The gradient is gone and the headline now uses the shop serif." });
    insp.setChanges(CHANGES, false);
    setState("done", "done");
    applyChips(agent.header.branchEl, agent.header.commitsEl, { ...agent.leaf, commitCount: 2 }, true);
    active.agentState = "done"; active.turnStartMs = 0; active.doneAtMs = Date.now();
    active.linesAdded = 46; active.linesDeleted = 23;
    renderSidebar();
  }

  async function play() {
    // 1. cursor clicks into the agent pane
    await wait(700);
    await cursorTo(agent.body, 700);
    await cursorClick();
    // 2. prompt types
    await typePrompt("make the holiday banner match the shop, and show me before and after");
    inspRow({ kind: "prompt", text: "make the holiday banner match the rest of the shop, and show me before and after" });
    await wait(400);
    // 3. agent works
    setState("working", "working");
    const spin = h("div", "wst__l");
    spin.innerHTML = `<span class="wst-work" data-spin>⠋</span> <span class="wst__sub">working…</span>`;
    agentWst.appendChild(spin); scrollDown(agent.body);
    let sf = 0;
    const spinner = setInterval(() => {
      const s = spin.querySelector("[data-spin]");
      if (s) s.textContent = SPIN[(sf = (sf + 1) % SPIN.length)];
    }, 90);

    await wait(650);
    agentLine(`<span class="wst-b">●</span> I'll capture the current banner first, then restyle it against the shop tokens.`);
    inspRow({ kind: "beat", time: "00:15", text: "I'll capture the current banner first, then restyle it against the shop tokens." });
    await wait(750);

    agentLine(`<span class="wst-b">●</span> <b>Bash</b>(node scripts/capture.mjs --route /?banner=holiday)`);
    agentLine(`<span class="wst__sub">&nbsp;&nbsp;└ captured design/banner-before.png (1280x400)</span>`);
    inspRow({ kind: "work", time: "00:15", verb: "Bash", target: "scripts/capture.mjs", note: "banner-before.png" });
    await wait(500);
    inspRow({ kind: "image", time: "00:15", svg: bannerBefore() });
    await wait(700);

    agentLine(`<span class="wst-b">●</span> <b>Read</b>(src/styles/tokens.css)`);
    agentLine(`<span class="wst__sub">&nbsp;&nbsp;└ Read 41 lines</span>`);
    inspRow({ kind: "work", time: "00:16", verb: "Read", target: "src/styles/tokens.css", note: "41 lines" });
    await wait(650);

    agentLine(`<span class="wst-b">●</span> <b>Update</b>(src/banner.css)`);
    agentLine(`<span class="wst__sub">&nbsp;&nbsp;└ Updated src/banner.css with <span class="wst-add">12 additions</span> and <span class="wst-del">7 removals</span></span>`);
    inspRow({ kind: "work", time: "00:16", verb: "Update", target: "src/banner.css", repeat: 2 });
    insp.setChanges(CHANGES.slice(0, 1), false);
    await wait(350);
    consoleLine(`<span class="wst__sub">14:32:07</span> <span class="wst-vite">[vite]</span> hmr update banner.css`);
    await wait(600);

    agentLine(`<span class="wst-b">●</span> <b>Update</b>(src/banner.ts)`);
    inspRow({ kind: "work", time: "00:17", verb: "Update", target: "src/banner.ts", note: "+6 −2" });
    insp.setChanges(CHANGES.slice(0, 2), false);
    await wait(350);
    consoleLine(`<span class="wst__sub">14:32:41</span> <span class="wst-vite">[vite]</span> hmr update banner.ts`);
    await wait(600);

    agentLine(`<span class="wst-b">●</span> <b>Bash</b>(node scripts/capture.mjs --route /?banner=holiday)`);
    agentLine(`<span class="wst__sub">&nbsp;&nbsp;└ captured design/banner-after.png (1280x400)</span>`);
    inspRow({ kind: "work", time: "00:17", verb: "Bash", target: "scripts/capture.mjs", note: "banner-after.png" });
    insp.setChanges(CHANGES, false);
    await wait(500);
    inspRow({ kind: "image", time: "00:18", svg: bannerAfter() });
    await wait(900);

    // 4. cursor demonstrates the Images filter, then restores All
    const imgFilter = insp.rail.querySelector<HTMLElement>('.inspector__filter[data-cat="images"]')!;
    const allFilter = insp.rail.querySelector<HTMLElement>(".inspector__filter--all")!;
    await cursorTo(imgFilter, 640);
    await cursorClick();
    // isolate images: switch off every other kind (Images stays on) so only the
    // two banner previews remain — reads as "clicked Images to filter".
    for (const cat of ["user", "claude", "actions", "skill"]) {
      const c = insp.rail.querySelector<HTMLElement>(`.inspector__filter[data-cat="${cat}"]`);
      if (c && c.getAttribute("aria-pressed") === "true") c.click();
    }
    scrollDown(insp.stream);
    await wait(1700);
    await cursorTo(allFilter, 520);
    await cursorClick();
    allFilter.click();   // All restores everything
    scrollDown(insp.stream);
    await wait(700);

    // 5. wrap up
    agentLine(`<span class="wst-b">●</span> The gradient is gone and the headline now uses the shop serif. The red stays on the call to action only. Before and after are in the journal.`);
    inspRow({ kind: "beat", time: "00:19", text: "The gradient is gone and the headline now uses the shop serif. The red stays on the call to action only." });
    clearInterval(spinner);
    spin.remove();
    setState("done", "done");
    applyChips(agent.header.branchEl, agent.header.commitsEl, { ...agent.leaf, commitCount: 2 }, true);
    active.agentState = "done"; active.turnStartMs = 0; active.doneAtMs = Date.now();
    active.linesAdded = 46; active.linesDeleted = 23;
    renderSidebar();
    scrollDown(agent.body);
    await wait(4200);
  }

  if (reduce) {
    renderFinal();
  } else {
    resetAll();
    (async () => {
      await wait(80);   // let layout settle so cursor math is correct
      while (true) { resetAll(); await play(); }
    })();
  }
  return win;
}
