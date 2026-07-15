/* Cloud resources panel.
 *
 * Answers one question: which machines are running right now, what agent made
 * each one, and what was it doing. Everything else is subordinate to that.
 *
 * Hierarchy is INSTANCE → AGENT, deliberately. The machine is the thing that
 * costs money and the thing you kill, so it leads; the agent is the explanation
 * and hangs off it on line two. (An earlier pass grouped BY agent and it read
 * backwards — you had to find the machine inside its explanation.)
 *
 * Two titled areas, and they differ in SHAPE, not just color: orphans are a
 * contained tinted box, live resources are a bare list. Knock the yellow out
 * entirely and you can still tell them apart.
 */
import { send, onMessage, type CloudResourceView, type CloudDataMessage } from "./bridge";
import { confirmDialog } from "./confirm";

let overlay: HTMLElement | null = null;
let latest: CloudResourceView[] = [];
/** Rows the user has asked to delete — greyed until the next poll drops them. */
const deleting = new Set<string>();
let tick: number | null = null;
/** Per-row refreshers for the wall-clock text (cost + uptime), rebuilt on every
 *  structural render. The 1s heartbeat runs these to keep cost accruing WITHOUT
 *  replacing the list DOM — a full rebuild each second dropped :hover for a
 *  frame, which is what made the hovered row's highlight blink. */
let liveUpdaters: Array<() => void> = [];

// ---------------------------------------------------------------- formatting

/** Hours a machine has been up, computed page-side so the number keeps moving
 * between polls rather than freezing for up to 5 minutes. */
function hoursUp(r: CloudResourceView): number {
  if (!r.createdMs) return 0;
  const ms = Date.now() - r.createdMs;
  return ms > 0 ? ms / 3_600_000 : 0;
}

function costSoFar(r: CloudResourceView): number {
  return r.usdPerHour * hoursUp(r);
}

/** A total spent. Past $100 the cents are noise, so they go — but never a lone
 * "$13.0", which reads as a truncation bug rather than a number. */
function money(usd: number): string {
  return usd >= 100 ? `$${Math.round(usd).toLocaleString()}` : `$${usd.toFixed(2)}`;
}

/** An hourly rate. Always 2dp: these are small numbers and $3.67 vs $3.7 is the
 * difference between reading as a price and reading as a rounding error. */
function rate(usd: number): string {
  return `$${usd.toFixed(2)}`;
}

function uptime(r: CloudResourceView): string {
  const h = hoursUp(r);
  if (h <= 0) return "—";
  if (h < 1) return `${Math.max(1, Math.round(h * 60))}m`;
  if (h < 24) {
    const hh = Math.floor(h);
    const mm = Math.round((h - hh) * 60);
    return mm ? `${hh}h ${mm}m` : `${hh}h`;
  }
  const d = Math.floor(h / 24);
  const hh = Math.round(h - d * 24);
  return hh ? `${d}d ${hh}h` : `${d}d`;
}

/** GPU / DATAPROC / VM. Neutral by design — the $/hr figure already says
 * "expensive", and a red GPU chip would compete with the orphan dot for the
 * one alarm this panel is allowed. */
function tagFor(r: CloudResourceView): { text: string; gpu: boolean } {
  if (r.isGpu) return { text: "GPU", gpu: true };
  if (r.kind === "cluster") return { text: "Dataproc", gpu: false };
  return { text: "VM", gpu: false };
}

/** "a2-highgpu-1g · us-central1-a · 2d 4h", or the VM count for a cluster. */
function specLine(r: CloudResourceView): string {
  const bits: string[] = [];
  if (r.kind === "cluster") bits.push(`${r.vmCount} VM${r.vmCount === 1 ? "" : "s"}`);
  else if (r.machineType) bits.push(r.machineType);
  if (r.zone) bits.push(r.zone);
  bits.push(uptime(r));
  return bits.join(" · ");
}

/** The "where" bit of the agent line. Empty for orphans on purpose: the area
 * header already says "No agent attached" and the dot is already yellow — a
 * third "no agent" per row is just noise. */
function whyLine(r: CloudResourceView): string {
  if (r.isOrphan) return "";
  const bits: string[] = [];
  if (r.paneId) bits.push(`pane ${r.paneId.slice(0, 4)}`);
  if (r.agentState) bits.push(r.agentState);
  return bits.join(" · ");
}

// ---------------------------------------------------------------- rendering

function el(tag: string, cls?: string, text?: string): HTMLElement {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
}

const KILL_SVG =
  '<svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="currentColor" ' +
  'stroke-width="1.4" stroke-linecap="round"><path d="M2.5 2.5l7 7M9.5 2.5l-7 7"/></svg>';

function renderInstance(r: CloudResourceView): HTMLElement {
  const card = el("div", "inst" + (r.isOrphan ? " inst--orphan" : ""));
  if (deleting.has(r.id)) card.classList.add("inst--deleting");

  // ---- line 1: the machine ----
  const top = el("div", "inst__top");
  const tag = tagFor(r);
  const chip = el("span", "tag" + (tag.gpu ? " tag--gpu" : ""), tag.text);
  top.appendChild(chip);
  top.appendChild(el("span", "inst__name", r.name));
  const spec = el("span", "inst__spec", specLine(r));
  top.appendChild(spec);

  const cost = el("span", "inst__cost");
  let costNode: Text | null = null;
  if (r.priceKnown) {
    costNode = document.createTextNode(money(costSoFar(r)));
    cost.appendChild(costNode);
    cost.appendChild(el("i", undefined, ` /${rate(r.usdPerHour)}`));
  } else {
    // Unknown machine type. An honest blank beats a fabricated $0.00.
    cost.appendChild(document.createTextNode("—"));
    cost.title = `No price on file for ${r.machineType}`;
  }
  top.appendChild(cost);

  // Wall-clock text refreshed in place by the 1s heartbeat: uptime (in specLine)
  // and the accruing cost. No-op for a priced-unknown row (its cost is static).
  liveUpdaters.push(() => {
    spec.textContent = specLine(r);
    if (costNode) costNode.textContent = money(costSoFar(r));
  });

  const kill = el("button", "inst__kill") as HTMLButtonElement;
  kill.innerHTML = KILL_SVG;
  kill.setAttribute("aria-label", `Delete ${r.name}`);
  kill.disabled = deleting.has(r.id);
  kill.addEventListener("click", () => void confirmDelete(r));
  top.appendChild(kill);
  card.appendChild(top);

  // ---- line 2: the agent that explains it ----
  const why = el("div", "inst__why");
  const dot = el("span", "inst__dot");
  dot.dataset.state = r.isOrphan ? "orphan" : (r.agentState ?? "idle");
  why.appendChild(dot);
  why.appendChild(el("span", "inst__agent", r.agentName || "unknown agent"));
  const where = whyLine(r);
  if (where) why.appendChild(el("span", "inst__where", where));
  if (r.task) {
    const task = el("span", "inst__task", `“${r.task}”`);
    task.title = r.task;   // the ledger keeps the full prompt; the row truncates
    why.appendChild(task);
  }
  card.appendChild(why);
  return card;
}

function areaHead(title: string, count: number, spent: number, orphans: boolean): HTMLElement {
  const head = el("div", "area__head");
  if (orphans) head.appendChild(el("span", "area__dot"));
  head.appendChild(el("span", "area__title", title));

  const meta = el("span", "area__meta");
  meta.appendChild(document.createTextNode(`${count} machine${count === 1 ? "" : "s"} · `));
  const b = el("b", undefined, money(spent));
  meta.appendChild(b);
  meta.appendChild(document.createTextNode(" spent"));
  head.appendChild(meta);

  if (orphans) {
    head.appendChild(el("span", "area__spacer"));
    const del = el("button", "cloud-btn", "Delete all") as HTMLButtonElement;
    del.addEventListener("click", () => void confirmDeleteAll(count, spent));
    head.appendChild(del);
  }
  return head;
}

/** Register a live refresher for an area head's "$X spent" total, which accrues
 *  in wall-clock between polls just like the per-row costs. */
function registerSpent(head: HTMLElement, group: CloudResourceView[]): void {
  const b = head.querySelector("b");
  if (b) liveUpdaters.push(() => {
    b.textContent = money(group.reduce((n, r) => n + costSoFar(r), 0));
  });
}

function renderBody(): HTMLElement {
  liveUpdaters = [];
  const body = el("div", "cloud__body");

  if (latest.length === 0) {
    const empty = el("div", "cloud__empty");
    empty.appendChild(el("div", "cloud__empty-title", "Nothing running"));
    empty.appendChild(el(
      "div",
      "cloud__empty-note",
      "Machines your agents create with gcloud show up here. Perch only tracks what it created itself.",
    ));
    body.appendChild(empty);
    return body;
  }

  const orphans = latest.filter((r) => r.isOrphan);
  const live = latest.filter((r) => !r.isOrphan);

  if (orphans.length) {
    const area = el("div", "area area--orphans");
    const head = areaHead(
      "No agent attached",
      orphans.length,
      orphans.reduce((n, r) => n + costSoFar(r), 0),
      true,
    );
    area.appendChild(head);
    registerSpent(head, orphans);
    orphans.forEach((r) => area.appendChild(renderInstance(r)));
    body.appendChild(area);
  }

  if (live.length) {
    const area = el("div", "area area--live");
    const head = areaHead(
      "Running for an agent",
      live.length,
      live.reduce((n, r) => n + costSoFar(r), 0),
      false,
    );
    area.appendChild(head);
    registerSpent(head, live);
    live.forEach((r) => area.appendChild(renderInstance(r)));
    body.appendChild(area);
  }

  return body;
}

/** Structural rebuild — only on new poll data or a delete. Replaces the list
 *  wholesale, which is fine because it happens on an EVENT, not a timer. */
function rerender(): void {
  if (!overlay) return;
  const card = overlay.querySelector(".cloud");
  const old = overlay.querySelector(".cloud__body");
  if (!card || !old) return;
  card.replaceChild(renderBody(), old);

  const sub = overlay.querySelector(".cloud__sub");
  if (sub) {
    const burn = latest.reduce((n, r) => n + r.usdPerHour, 0);
    sub.textContent = latest.length
      ? `${latest.length} running · ${rate(burn)}/hr`
      : "nothing running";
  }
}

/** The 1s heartbeat: refresh only the wall-clock-derived text (cost + uptime) in
 *  place. Keeping the existing DOM rather than replaceChild is what stops the
 *  hovered row's highlight from blinking once a second. */
function retick(): void {
  if (!overlay) return;
  for (const fn of liveUpdaters) fn();
}

// ---------------------------------------------------------------- actions

async function confirmDelete(r: CloudResourceView): Promise<void> {
  const what = r.kind === "cluster"
    ? `the Dataproc cluster ${r.name} (${r.vmCount} VMs)`
    : `the VM ${r.name}`;
  const ok = await confirmDialog({
    title: "Delete this machine?",
    body: `This permanently deletes ${what} in ${r.zone}. Anything running on it dies with it.`,
    confirmLabel: "Delete",
    danger: true,
  });
  if (!ok) return;
  deleting.add(r.id);
  rerender();
  send({ type: "cloud.delete", id: r.id });
}

async function confirmDeleteAll(count: number, spent: number): Promise<void> {
  const ok = await confirmDialog({
    title: `Delete ${count} orphaned machine${count === 1 ? "" : "s"}?`,
    body:
      `Nothing is using ${count === 1 ? "it" : "them"} — the agents that made ` +
      `${count === 1 ? "it" : "them"} are gone. ${money(spent)} spent so far. This cannot be undone.`,
    confirmLabel: "Delete all",
    danger: true,
  });
  if (!ok) return;
  latest.filter((r) => r.isOrphan).forEach((r) => deleting.add(r.id));
  rerender();
  send({ type: "cloud.deleteOrphans" });
}

// ---------------------------------------------------------------- lifecycle

export function applyCloudData(msg: CloudDataMessage): void {
  latest = msg.resources ?? [];
  // A row we asked to delete is gone from the poll → forget it.
  for (const id of [...deleting]) if (!latest.some((r) => r.id === id)) deleting.delete(id);
  updateSidebar();
  rerender();
}

export function closeCloudPanel(): void {
  if (!overlay) return;
  overlay.remove();
  overlay = null;
  if (tick != null) { window.clearInterval(tick); tick = null; }
  send({ type: "cloud.panel", open: false });
}

export function showCloudPanel(): void {
  if (overlay) { closeCloudPanel(); return; }

  overlay = el("div", "cloud-overlay");
  overlay.addEventListener("click", (e) => { if (e.target === overlay) closeCloudPanel(); });

  const card = el("div", "cloud");
  card.setAttribute("role", "dialog");
  card.setAttribute("aria-modal", "true");
  card.setAttribute("aria-label", "Cloud resources");

  const head = el("div", "cloud__head");
  head.appendChild(el("span", "cloud__title", "Cloud"));
  head.appendChild(el("span", "cloud__sub", "…"));
  head.appendChild(el("span", "cloud__spacer"));

  const refresh = el("button", "cloud__icon") as HTMLButtonElement;
  refresh.setAttribute("aria-label", "Refresh");
  refresh.innerHTML =
    '<svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" ' +
    'stroke-width="1.3" stroke-linecap="round"><path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9"/>' +
    '<path d="M13.6 2.2v2.8h-2.8"/></svg>';
  refresh.addEventListener("click", () => send({ type: "cloud.refresh" }));
  head.appendChild(refresh);
  card.appendChild(head);

  card.appendChild(renderBody());

  const foot = el("div", "cloud__foot");
  foot.innerHTML =
    '<svg width="11" height="11" viewBox="0 0 16 16" fill="none" stroke="currentColor" ' +
    'stroke-width="1.3"><circle cx="8" cy="8" r="6.4"/><path d="M8 7.2v4" stroke-linecap="round"/>' +
    '<circle cx="8" cy="4.9" r=".7" fill="currentColor" stroke="none"/></svg>';
  const note = el("span", undefined, "Costs are estimates");
  note.title =
    "List price for the machine type × time running. Excludes disks, network egress, " +
    "and any sustained-use or committed-use discounts — your real bill will differ. " +
    "Perch only shows machines it created; anything made outside Perch is invisible here.";
  foot.appendChild(note);
  card.appendChild(foot);

  overlay.appendChild(card);
  document.body.appendChild(overlay);

  const esc = (e: KeyboardEvent) => {
    if (e.key !== "Escape" || !overlay) return;
    closeCloudPanel();
    window.removeEventListener("keydown", esc);
  };
  window.addEventListener("keydown", esc);

  // Cost and uptime are derived from wall-clock, so refresh once a second while
  // open — otherwise a machine's cost sits frozen between 60s polls. retick (not
  // rerender) so the hovered row survives the tick.
  tick = window.setInterval(retick, 1000);

  send({ type: "cloud.panel", open: true });
  rerender();
}

// ---------------------------------------------------------------- sidebar area

/** The sidebar CLOUD area.
 *
 * It does not exist unless machines are running — which means its mere presence
 * already says "you are being billed". That's why the resting state wears the
 * teal --color-cloud class instead of a neutral card: there is no such thing as
 * an idle cloud area.
 *
 * Orphans escalate it to caution AND grow an extra row, so the thing that needs
 * you changes the area's SIZE, not just its color. */
function updateSidebar(): void {
  const area = document.getElementById("cloud-area");
  const card = document.getElementById("cloud-card");
  if (!area || !card) return;

  if (latest.length === 0) {
    area.hidden = true;
    return;
  }
  area.hidden = false;

  const orphans = latest.filter((r) => r.isOrphan);
  const burn = latest.reduce((n, r) => n + r.usdPerHour, 0);
  const spent = latest.reduce((n, r) => n + costSoFar(r), 0);

  const title = document.getElementById("cloud-card-title");
  const rateEl = document.getElementById("cloud-card-rate");
  const sub = document.getElementById("cloud-card-sub");
  if (title) title.textContent = `${latest.length} machine${latest.length === 1 ? "" : "s"}`;
  if (rateEl) rateEl.textContent = `${rate(burn)}/hr`;

  if (sub) {
    // Composition + what it has cost so far. NOT "today" — we keep no history,
    // only what each currently-running machine has accrued since it started, and
    // claiming a daily total we can't compute would be a lie.
    const gpu = latest.filter((r) => r.isGpu).length;
    const clusters = latest.filter((r) => r.kind === "cluster").length;
    const bits: string[] = [];
    if (gpu) bits.push(`${gpu} GPU`);
    if (clusters) bits.push(`${clusters} cluster${clusters === 1 ? "" : "s"}`);
    bits.push(`${money(spent)} spent`);
    sub.textContent = bits.join(" · ");
  }

  card.classList.toggle("cloud-card--orphan", orphans.length > 0);

  const alert = document.getElementById("cloud-card-alert");
  const alertText = document.getElementById("cloud-card-alert-text");
  const alertCost = document.getElementById("cloud-card-alert-cost");
  if (alert) alert.hidden = orphans.length === 0;
  if (orphans.length && alertText && alertCost) {
    alertText.textContent = `${orphans.length} with no agent`;
    alertCost.textContent = money(orphans.reduce((n, r) => n + costSoFar(r), 0));
  }

  card.title = orphans.length
    ? `${orphans.length} machine${orphans.length === 1 ? "" : "s"} running with no agent attached`
    : `${latest.length} machine${latest.length === 1 ? "" : "s"} running`;
}

export function initCloud(): void {
  document.getElementById("cloud-card")?.addEventListener("click", () => showCloudPanel());
  onMessage((msg) => {
    if (msg.type === "cloud.data") applyCloudData(msg);
  });
  // Cost accrues in wall-clock, so the sidebar figure would otherwise sit frozen
  // between polls (up to 5 minutes). Cheap: it's a few DOM writes, and only while
  // machines actually exist.
  window.setInterval(() => {
    if (latest.length) updateSidebar();
  }, 10_000);
}
