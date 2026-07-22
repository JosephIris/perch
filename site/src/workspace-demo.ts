// The interactive hero: a live replica of the perch workspace, assembled from
// the app's REAL components (Sidebar, pane headers, the mascot boot overlay)
// plus the hand-built inspector journal. Not a screenshot — the spinners tick,
// the timestamps age, the inspector filters work, and the mascot walks its
// power line. Fixtures are the storefront "holiday banner" scenario.

import { Sidebar } from "../../src/web/src/sidebar.js";
import {
  buildPaneHeader, applyAgentBadge, applyChips, applyPorts, applyModelChip,
} from "../../src/web/src/pane-header.js";
import { createSetupOverlay } from "../../src/web/src/setup-overlay.js";
import { buildInspector } from "./inspector-demo.js";

const h = (tag: string, cls = "", html = ""): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (html) e.innerHTML = html;
  return e;
};

/** A terminal pane: real pane header (fixtured) + a body of styled text. */
function pane(opts: {
  color: number; name: string; agent?: string; model?: string;
  branch?: string; commits?: number; ports?: number[];
  state: string; stateLabel?: string; bodyHtml?: string; large?: boolean;
}): { el: HTMLElement; body: HTMLElement } {
  const el = h("div", "wspane" + (opts.large ? " wspane--large" : ""));
  el.dataset.color = String(opts.color);
  el.classList.add("pane");   // so .pane[data-color] name-color rule applies

  const leaf: any = {
    kind: "leaf", paneId: opts.name, colorIndex: opts.color,
    branch: opts.branch ?? "", commitCount: opts.commits ?? 0,
    ports: opts.ports ?? [], agentType: opts.agent ?? "", model: opts.model ?? "",
  };
  const hd = buildPaneHeader(opts.name);
  hd.colorDotEl.dataset.color = String(opts.color);
  hd.nameEl.textContent = opts.name;
  applyAgentBadge(hd.agentBadgeEl, opts.agent);
  applyModelChip(hd.modelEl as HTMLButtonElement, opts.agent, opts.model);
  applyChips(hd.branchEl, hd.commitsEl, leaf, true);
  applyPorts(hd.portsEl as HTMLButtonElement, leaf);
  hd.stateDotEl.dataset.state = opts.state;
  hd.stateLabelEl.textContent = opts.stateLabel ?? "";
  el.appendChild(hd.root);

  const body = h("div", "wspane__body");
  if (opts.bodyHtml) body.innerHTML = opts.bodyHtml;
  el.appendChild(body);
  return { el, body };
}

/* --- terminal bodies (styled text, the storefront story) --------------- */
const MAIN_TERM = `
<div class="wst">
<div class="wst__l"><span class="wst-user">&gt; make the holiday banner match the shop, and show me before and after</span></div>
<div class="wst__l"><span class="wst-b">●</span> I'll capture the current banner first, then restyle it against the shop tokens.</div>
<div class="wst__l"><span class="wst-b">●</span> <b>Bash</b>(node scripts/capture.mjs --route /?banner=holiday)</div>
<div class="wst__l wst__sub">└ captured design/banner-before.png (1280x400)</div>
<div class="wst__l"><span class="wst-b">●</span> <b>Read</b>(src/styles/tokens.css)</div>
<div class="wst__l wst__sub">└ Read 41 lines</div>
<div class="wst__l"><span class="wst-b">●</span> <b>Update</b>(src/banner.css)</div>
<div class="wst__l wst__sub">└ Updated src/banner.css with <span class="wst-add">12 additions</span> and <span class="wst-del">7 removals</span></div>
<div class="wst__l"><span class="wst-b">●</span> <b>Bash</b>(node scripts/capture.mjs --route /?banner=holiday)</div>
<div class="wst__l wst__sub">└ captured design/banner-after.png (1280x400)</div>
<div class="wst__l"><span class="wst-b">●</span> The gradient is gone and the headline now uses the shop serif. The red stays on the call-to-action only. Before and after are in the journal.</div>
<div class="wst__l"><span class="wst-user">&gt; love it. tighten the mobile crop a little</span></div>
<div class="wst__l"><span class="wst-work">✳</span> Adjusting the mobile breakpoint… <span class="wst__sub">(1m 02s · esc to interrupt)</span></div>
</div>`;

const DEV_TERM = `
<div class="wst">
<div class="wst__l"><span class="wst__sub">PS C:\\dev\\storefront-web&gt;</span> npm run dev</div>
<div class="wst__l">&nbsp;</div>
<div class="wst__l"><span class="wst-vite">VITE v6.0.3</span> <span class="wst__sub">ready in</span> 241 ms</div>
<div class="wst__l">&nbsp;</div>
<div class="wst__l"><span class="wst-arrow">→</span> <span class="wst__sub">Local:</span>   <span class="wst-link">http://localhost:5173/</span></div>
<div class="wst__l"><span class="wst-arrow">→</span> <span class="wst__sub">Network:</span> use --host to expose</div>
<div class="wst__l">&nbsp;</div>
<div class="wst__l"><span class="wst__sub">14:32:07</span> <span class="wst-vite">[vite]</span> hmr update banner.css</div>
<div class="wst__l"><span class="wst__sub">14:32:41</span> <span class="wst-vite">[vite]</span> hmr update banner.ts</div>
</div>`;

/** Static-ish sidebar top chrome + resource cards (not part of the Sidebar
 *  class, so re-created to the app's look). Keeps the hero reading as the whole
 *  app, not just the list. */
function sidebarTop(): HTMLElement {
  return h("div", "wsside__top", `
    <div class="wsside__seg"><button class="wsside__seg-btn">Sessions</button><button class="wsside__seg-btn wsside__seg-btn--on">Projects</button></div>
    <div class="wsside__action"><span class="wsside__plus">+</span> New session</div>
    <div class="wsside__action"><span class="wsside__grid"></span> Dashboard <span class="wsside__badge">5</span></div>
  `);
}
function sidebarResources(): HTMLElement {
  return h("div", "wsside__res", `
    <div class="wsside__reslabel">Cloud</div>
    <div class="wscard wscard--cloud">
      <span class="wscard__dot"></span>
      <span class="wscard__name">2 machines</span>
      <span class="wscard__meta">$0.41/hr</span>
      <span class="wscard__sub">e2-standard-4 · build-runner</span>
    </div>
    <div class="wsside__reslabel">Local</div>
    <div class="wscard wscard--local">
      <span class="wscard__dot"></span>
      <span class="wscard__name">vite</span>
      <span class="wscard__meta">:5173</span>
      <span class="wscard__sub">holiday banner</span>
    </div>
  `);
}

export function buildWorkspaceDemo(sessions: any[], projects: any[]): HTMLElement {
  const win = h("div", "wsdemo");

  // Title bar (the WPF FluentWindow chrome).
  win.appendChild(h("div", "wsdemo__titlebar", `
    <span class="wsdemo__brand"><span class="wsdemo__glyph"></span>perch</span>
    <span class="wsdemo__winbtns"><i></i><i></i><i class="wsdemo__x"></i></span>
  `));

  const bodyGrid = h("div", "wsdemo__body");

  // Sidebar (real component in the middle).
  const side = h("aside", "wsdemo__sidebar sidebar");
  side.appendChild(sidebarTop());
  const scroll = h("div", "sidebar__scroll");
  const listEl = h("div");
  const closedEl = h("div");
  scroll.append(listEl, closedEl);
  side.appendChild(scroll);
  side.appendChild(sidebarResources());
  const sb = new Sidebar(listEl, h("button"), closedEl);
  sb.render(sessions, "holiday-banner", [], projects, "projects");

  // Workspace: main pane (left) + right column (dev + boot).
  const work = h("div", "wsdemo__workspace");
  const main = pane({
    color: 0, name: "holiday banner", agent: "claude", model: "opus",
    branch: "main", commits: 2, state: "working", stateLabel: "working",
    bodyHtml: MAIN_TERM, large: true,
  });
  const rightCol = h("div", "wsdemo__rightcol");
  const dev = pane({
    color: 2, name: "dev server", branch: "main", ports: [5173],
    state: "idle", bodyHtml: DEV_TERM,
  });
  const boot = pane({
    color: 5, name: "gift guide draft", agent: "claude", state: "idle",
    bodyHtml: `<div class="wst"><div class="wst__l"><span class="wst__sub">PS C:\\dev\\storefront-web&gt;</span> claude</div></div>`,
  });
  // Boot pane wears the real frosted "Setting up" overlay + mascot.
  const overlay = createSetupOverlay();
  boot.body.classList.add("wspane__body--boot");
  boot.body.appendChild(overlay.el);
  overlay.show();

  rightCol.append(dev.el, boot.el);
  work.append(main.el, rightCol);

  // Inspector (full, changes expanded).
  const insp = h("div", "wsdemo__inspector");
  insp.appendChild(buildInspector({ changesOpen: true }));

  bodyGrid.append(side, work, insp);
  win.appendChild(bodyGrid);
  return win;
}
