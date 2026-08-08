// Settings dialog — a modal over the workspace exposing the three
// app-level defaults the user asked for: default shell, default working
// directory, default terminal font size.
//
// Pattern (CLAUDE.md "Settings pages"): WPF-UI CardControl rows — one
// setting per row, label + description on the left, control on the right.
// Centered modal (Constitution allows centering for dialogs only). All
// values come from design tokens; nothing hardcoded.
//
// Data flow: open() sends settings.request; the host replies with a
// settings.data message that main.ts routes here via applyData(). Save
// ships settings.save and closes. Shell/cwd changes affect NEW sessions
// only (lazy spawn reads them) — the dialog says so inline.

import { modKeyLabel, send } from "./bridge.js";
import type { SettingsDataMessage, InMessage, NewTabPosition } from "./bridge.js";
import { MIN_FONT_SIZE, MAX_FONT_SIZE, DEFAULT_FONT_SIZE } from "./pane.js";
import { Dropdown } from "./dropdown.js";
import { showOnboarding } from "./onboarding.js";
import { confirmDialog } from "./confirm.js";
import { buildSettingsMascot } from "./settings-mascot.js";

let overlay: HTMLElement | null = null;
let shellDropdown: Dropdown | null = null;
let cwdInput: HTMLInputElement | null = null;
let scanRootsInput: HTMLTextAreaElement | null = null;
let wtRootInput: HTMLInputElement | null = null;
let seedsInput: HTMLTextAreaElement | null = null;
let projectListEl: HTMLElement | null = null;
let fontInput: HTMLInputElement | null = null;
let resumeToggle: HTMLButtonElement | null = null;
let newTabDropdown: Dropdown | null = null;
let updateCheckBtn: HTMLButtonElement | null = null;
let updateStatusEl: HTMLElement | null = null;
// Default Updates-row blurb; restated after a check resets the row. Mirrors the
// host's actual cadence (launch + hourly timer + on-refocus).
const UPDATE_CADENCE = "Checks on launch, hourly, and when you return to Perch.";

/** Open the settings dialog. Renders a shell immediately (so the modal
 *  appears instantly) and requests fresh data from the host to fill it. */
export function openSettings(): void {
  if (overlay) return; // already open
  buildSkeleton();
  send({ type: "settings.request" });
}

export function closeSettings(): void {
  if (!overlay) return;
  overlay.classList.add("settings-page--closing");
  const el = overlay;
  overlay = null;
  shellDropdown?.dispose();
  shellDropdown = null;
  cwdInput = null;
  scanRootsInput = null;
  wtRootInput = null;
  seedsInput = null;
  projectListEl = null;
  fontInput = null;
  resumeToggle = null;
  newTabDropdown?.dispose();
  newTabDropdown = null;
  updateCheckBtn = null;
  updateStatusEl = null;
  document.removeEventListener("keydown", onKeyDown, true);
  // Let the fade-out finish before removing — matches dur-normal.
  el.addEventListener("animationend", () => el.remove(), { once: true });
  // Fallback in case animationend doesn't fire (reduced motion etc.).
  window.setTimeout(() => el.remove(), 260);
}

/** Fill the open dialog with host data. No-op if the dialog was closed
 *  before the reply arrived. */
export function applySettingsData(msg: SettingsDataMessage): void {
  if (!overlay || !shellDropdown || !cwdInput || !fontInput || !resumeToggle) return;

  // First option = auto-detect (empty command line), then each detected
  // shell. If the stored shell is a custom command line we don't
  // recognize, surface it as its own selectable option so it isn't lost.
  const options = [
    { value: "", label: "Auto-detect (first available)" },
    ...msg.shells.map((s) => ({ value: s.cmd, label: s.name })),
  ];
  if (msg.defaultShell && !msg.shells.some((s) => s.cmd === msg.defaultShell)) {
    options.push({ value: msg.defaultShell, label: `Custom: ${msg.defaultShell}` });
  }
  shellDropdown.setOptions(options, msg.defaultShell ?? "");

  cwdInput.value = msg.defaultCwd ?? "";
  cwdInput.placeholder = msg.defaultCwdResolved || "%USERPROFILE%";
  if (scanRootsInput) scanRootsInput.value = (msg.projectScanRoots ?? []).join("\n");
  if (wtRootInput) {
    wtRootInput.value = msg.worktreeRoot ?? "";
    wtRootInput.placeholder = msg.worktreeRootResolved ?? "";
  }
  if (seedsInput) seedsInput.value = (msg.worktreeSeedPaths ?? []).join("\n");
  renderProjectList(msg);

  fontInput.value = String(msg.fontSize || DEFAULT_FONT_SIZE);

  // Default the toggle ON when the host omits the flag — matches the
  // Settings.ResumeAgentsOnLaunch code default (resume is opt-out).
  setToggle(resumeToggle, msg.resumeAgentsOnLaunch ?? true);

  // Absent → "top", matching the host's Settings.NewTabPosition default.
  newTabDropdown?.setOptions(
    [
      { value: "top", label: "Top of the project" },
      { value: "bottom", label: "Bottom of the project" },
    ],
    msg.newTabPosition ?? "top",
  );

  // Updates row: show the running version + cadence, and disable "Check now"
  // on a copy that can't self-update (dev `dotnet run` / portable unzip).
  if (updateStatusEl && updateCheckBtn) {
    const ver = msg.appVersion ? `Perch ${msg.appVersion}. ` : "";
    if (msg.updatable === false) {
      updateStatusEl.textContent =
        `${ver}Updates are managed outside this build.`.trim();
      updateCheckBtn.disabled = true;
    } else {
      updateStatusEl.textContent = `${ver}${UPDATE_CADENCE}`;
      updateCheckBtn.disabled = false;
    }
  }
}

/** Reflect a manual-check result (host `update.status`) in the Updates row.
 *  No-op if the dialog was closed before the reply arrived. */
export function applyUpdateStatus(msg: Extract<InMessage, { type: "update.status" }>): void {
  if (!overlay || !updateCheckBtn || !updateStatusEl) return;
  updateCheckBtn.disabled = false;
  updateCheckBtn.textContent = "Check now";
  switch (msg.state) {
    case "uptodate":
      updateStatusEl.textContent = msg.version
        ? `Perch ${msg.version} is up to date.`
        : "Perch is up to date.";
      break;
    case "available":
      updateStatusEl.textContent = `Update to v${msg.version} ready — use the pill in the sidebar.`;
      break;
    case "error":
      updateStatusEl.textContent = "Couldn't reach the update feed. Try again.";
      break;
    case "unsupported":
      updateCheckBtn.disabled = true;
      updateStatusEl.textContent = "Updates are managed outside this build.";
      break;
  }
}

/** The registered-projects manager inside Settings. Each row: name (editable),
 *  path, a per-project seed override, and unregister. */
function renderProjectList(msg: SettingsDataMessage): void {
  if (!projectListEl) return;
  const projects = msg.projects ?? [];
  projectListEl.replaceChildren();

  if (!projects.length) {
    const empty = document.createElement("div");
    empty.className = "settings-projects__empty";
    empty.textContent = "None yet. Switch the sidebar to Projects to add one.";
    projectListEl.appendChild(empty);
    return;
  }

  for (const p of projects) {
    const row = document.createElement("div");
    row.className = "settings-project";

    const head = document.createElement("div");
    head.className = "settings-project__head";

    const name = document.createElement("input");
    name.type = "text";
    name.className = "settings-control settings-control--text settings-project__name";
    name.value = p.name;
    name.spellcheck = false;
    name.setAttribute("aria-label", `Name of ${p.name}`);
    // Commit on blur / Enter rather than per-keystroke — a rename shouldn't fire
    // a host round-trip (and a state push) for every character typed.
    const commitName = () => {
      const next = name.value.trim();
      if (next && next !== p.name) send({ type: "project.update", id: p.id, name: next });
    };
    name.addEventListener("blur", commitName);
    name.addEventListener("keydown", (e) => {
      if (e.key === "Enter") name.blur();
    });
    head.appendChild(name);

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "settings-btn settings-btn--subtle settings-project__remove";
    remove.textContent = "Unregister";
    remove.addEventListener("click", async () => {
      const ok = await confirmDialog({
        title: `Unregister ${p.name}?`,
        // Say plainly what does NOT happen — this reads destructive but isn't.
        body:
          "Its tabs stay open and move to “Other”. Nothing on disk is touched: " +
          "no worktree, branch, or file is deleted.",
        confirmLabel: "Unregister",
        cancelLabel: "Keep",
      });
      if (ok) send({ type: "project.remove", id: p.id });
    });
    head.appendChild(remove);
    row.appendChild(head);

    const path = document.createElement("div");
    path.className = "settings-project__path";
    path.textContent = p.path;
    path.title = p.path;   // full path on hover; the row ellipsizes
    row.appendChild(path);

    // A REAL label. The seed box previously had none — just grey placeholder
    // text doing double duty, which is an anti-pattern precisely because the
    // label vanishes the moment you type.
    const seedLabel = document.createElement("label");
    seedLabel.className = "settings-project__sublabel";
    const defaults = (msg.worktreeSeedPaths ?? []).join(", ") || "nothing";
    seedLabel.textContent = "Copy into its worktrees";

    const seedHint = document.createElement("span");
    seedHint.className = "settings-project__subhint";
    seedHint.textContent = `Leave blank to use the default (${defaults}).`;
    seedLabel.appendChild(seedHint);

    const seeds = document.createElement("textarea");
    seeds.className =
      "settings-control settings-control--text settings-control--area settings-project__seeds";
    seeds.rows = 2;
    seeds.spellcheck = false;
    seeds.value = (p.seedPaths ?? []).join("\n");
    seeds.placeholder = "e.g. src/web/node_modules";
    seedLabel.appendChild(seeds);

    const commitSeeds = () => {
      const next = seeds.value.split("\n").map((s) => s.trim()).filter(Boolean);
      const before = (p.seedPaths ?? []).join("\n");
      if (next.join("\n") === before) return;   // don't push a no-op on every blur
      send({ type: "project.update", id: p.id, seedPaths: next });
    };
    seeds.addEventListener("blur", commitSeeds);
    row.appendChild(seedLabel);

    projectListEl.appendChild(row);
  }
}

function save(): void {
  if (!shellDropdown || !cwdInput || !fontInput) return;
  let fontSize = parseInt(fontInput.value, 10);
  if (!Number.isFinite(fontSize)) fontSize = DEFAULT_FONT_SIZE;
  fontSize = Math.max(MIN_FONT_SIZE, Math.min(MAX_FONT_SIZE, fontSize));
  send({
    type: "settings.save",
    defaultShell: shellDropdown.value,
    defaultCwd: cwdInput.value.trim(),
    fontSize,
    resumeAgentsOnLaunch: resumeToggle ? getToggle(resumeToggle) : undefined,
    newTabPosition: newTabDropdown
      ? (newTabDropdown.value as NewTabPosition)
      : undefined,
    // Split on newlines, drop blanks — so a trailing newline or an accidental
    // empty line doesn't register "" as a scan root.
    projectScanRoots: scanRootsInput
      ? scanRootsInput.value.split("\n").map((s) => s.trim()).filter(Boolean)
      : undefined,
    worktreeRoot: wtRootInput ? wtRootInput.value.trim() : undefined,
    worktreeSeedPaths: seedsInput
      ? seedsInput.value.split("\n").map((s) => s.trim()).filter(Boolean)
      : undefined,
  });
  closeSettings();
}

/**
 * Settings is a PAGE, not a dialog.
 *
 * It used to be a modal card, and once the project/worktree settings landed it
 * had ~1300px of content to show in a ~600px window — so it stretched floor to
 * ceiling in the middle of the screen, scrolled as one long undifferentiated
 * list, and pushed Save below the fold. A modal is the wrong container for that
 * much material.
 *
 * So: a full-surface page with a category rail on the left and ONE category at a
 * time on the right (the Win11 Settings / VS Code shape). Each pane is short
 * enough to read without scrolling, related settings sit together, and the page
 * gets room to breathe. Confirmations stay modal — `.settings-card` is still
 * used by confirm.ts, which is exactly the kind of short, interrupting thing a
 * dialog IS right for.
 */
type PaneId = "general" | "projects" | "worktrees" | "sessions" | "about";

const PANES: { id: PaneId; label: string }[] = [
  { id: "general", label: "General" },
  { id: "projects", label: "Projects" },
  { id: "worktrees", label: "Worktrees" },
  { id: "sessions", label: "Sessions" },
  { id: "about", label: "About" },
];

let activePane: PaneId = "general";

function buildSkeleton(): void {
  overlay = document.createElement("div");
  overlay.className = "settings-page";
  overlay.setAttribute("role", "dialog");
  overlay.setAttribute("aria-modal", "true");
  overlay.setAttribute("aria-label", "Settings");

  // ── Header: title + close ────────────────────────────────────────────────
  const head = document.createElement("header");
  head.className = "settings-page__head";

  const title = document.createElement("h1");
  title.className = "settings-page__title";
  title.textContent = "Settings";
  head.appendChild(title);

  const closeBtn = document.createElement("button");
  closeBtn.type = "button";
  closeBtn.className = "settings-page__close";
  closeBtn.setAttribute("aria-label", "Close settings");
  closeBtn.textContent = "✕";
  closeBtn.addEventListener("click", () => closeSettings());
  head.appendChild(closeBtn);

  overlay.appendChild(head);

  // ── Body: category rail + panes ──────────────────────────────────────────
  const main = document.createElement("div");
  main.className = "settings-page__main";
  overlay.appendChild(main);

  const nav = document.createElement("nav");
  nav.className = "settings-page__nav";
  nav.setAttribute("aria-label", "Settings categories");
  main.appendChild(nav);

  const content = document.createElement("div");
  content.className = "settings-page__content";
  main.appendChild(content);

  // The resident: Monocle Guy balancing a wrench, bottom-right of the main
  // area on every pane. Decorative; CSS hides him when the window is too
  // narrow for him to stay clear of the setting rows.
  const mascot = document.createElement("div");
  mascot.className = "settings-page__mascot";
  mascot.setAttribute("aria-hidden", "true");
  mascot.appendChild(buildSettingsMascot());
  main.appendChild(mascot);

  const panes = new Map<PaneId, HTMLElement>();
  const navButtons = new Map<PaneId, HTMLButtonElement>();

  for (const p of PANES) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "settings-nav-btn";
    btn.textContent = p.label;
    // aria-selected is the source of truth the CSS selects on, so the rail can
    // never highlight one category while another is showing.
    btn.setAttribute("aria-selected", String(p.id === activePane));
    btn.addEventListener("click", () => showPane(p.id));
    nav.appendChild(btn);
    navButtons.set(p.id, btn);

    const pane = document.createElement("section");
    pane.className = "settings-pane";
    pane.hidden = p.id !== activePane;
    content.appendChild(pane);
    panes.set(p.id, pane);
  }

  function showPane(id: PaneId) {
    activePane = id;
    for (const [pid, el] of panes) el.hidden = pid !== id;
    for (const [pid, btn] of navButtons)
      btn.setAttribute("aria-selected", String(pid === id));
  }

  const general = panes.get("general")!;
  const projects = panes.get("projects")!;
  const worktrees = panes.get("worktrees")!;
  const sessions = panes.get("sessions")!;
  const about = panes.get("about")!;

  // ── General ─────────────────────────────────────────────────────────────
  // Custom dropdown (not native <select>) so the option popup renders on our
  // dark surface — WebView2 paints native select popups with an OS-white
  // background CSS can't reach.
  shellDropdown = new Dropdown();
  general.appendChild(
    makeRow(
      "Default shell",
      "Used for new sessions. Existing sessions keep their shell.",
      shellDropdown.element,
    ),
  );

  cwdInput = document.createElement("input");
  cwdInput.type = "text";
  cwdInput.className = "settings-control settings-control--text";
  cwdInput.spellcheck = false;
  cwdInput.autocomplete = "off";
  general.appendChild(
    makeRow(
      "Default working directory",
      "Where new sessions start when no directory is recorded.",
      cwdInput,
    ),
  );

  fontInput = document.createElement("input");
  fontInput.type = "number";
  fontInput.min = String(MIN_FONT_SIZE);
  fontInput.max = String(MAX_FONT_SIZE);
  fontInput.step = "1";
  fontInput.className = "settings-control settings-control--number";
  general.appendChild(
    makeRow(
      "Terminal font size",
      `Pixels. Also adjustable with ${modKeyLabel} + and ${modKeyLabel} − (${MIN_FONT_SIZE}–${MAX_FONT_SIZE}).`,
      fontInput,
    ),
  );

  // ── Projects ────────────────────────────────────────────────────────────
  // Scan folders: a LIST, not a single root, because code lives in several
  // places on a dev machine (work repos here, side projects there). Each is
  // scanned one level deep when you hit "Find repos" in project mode.
  scanRootsInput = document.createElement("textarea");
  scanRootsInput.className = "settings-control settings-control--text settings-control--area";
  scanRootsInput.rows = 3;
  scanRootsInput.spellcheck = false;
  // "e.g." so an empty box doesn't read as a CONFIGURED value — grey paths that
  // look real are worse than none.
  scanRootsInput.placeholder = "e.g. C:\\Users\\you\\dev-projects";

  // Scan folders on their own register NOTHING — they only say where to look —
  // and that gap is a trap: you set two folders, save, and nothing appears.
  // So the action lives right next to the setting that enables it.
  const scanWrap = document.createElement("div");
  scanWrap.className = "settings-scan";
  scanWrap.appendChild(scanRootsInput);

  const scanBtn = document.createElement("button");
  scanBtn.type = "button";
  scanBtn.className = "settings-btn settings-btn--subtle settings-scan__btn";
  scanBtn.textContent = "Find repos…";
  scanBtn.addEventListener("click", () => {
    // Save first: the scan runs host-side against the SAVED roots, so scanning
    // with the box's unsaved contents would search the old folders and look
    // broken. save() also closes the page, which is what we want — the picker
    // then opens over the workspace rather than behind this page.
    save();
    send({ type: "projects.scan" });
  });
  scanWrap.appendChild(scanBtn);

  projects.appendChild(
    makeWideRow(
      "Scan folders",
      "One per line, searched one level deep. Listing a folder doesn’t register anything — use Find repos to pick.",
      scanWrap,
    ),
  );

  projectListEl = document.createElement("div");
  projectListEl.className = "settings-projects";
  projects.appendChild(
    makeWideRow(
      "Registered projects",
      "Unregistering leaves every tab open and deletes nothing on disk.",
      projectListEl,
    ),
  );

  // ── Worktrees ───────────────────────────────────────────────────────────
  const wtBlurb = document.createElement("p");
  wtBlurb.className = "settings-pane__blurb";
  wtBlurb.textContent =
    "Each project tab can run in its own git worktree — a separate folder on its " +
    "own branch — so two agents can't overwrite each other's files, and each tab's " +
    "change counts are its own.";
  worktrees.appendChild(wtBlurb);

  // Only affects tabs made FROM NOW ON: existing ones carry an absolute path, so
  // moving this can't strand them.
  wtRootInput = document.createElement("input");
  wtRootInput.type = "text";
  wtRootInput.className = "settings-control settings-control--text";
  wtRootInput.spellcheck = false;
  wtRootInput.autocomplete = "off";
  worktrees.appendChild(
    makeWideRow(
      "Worktree folder",
      "Where a project tab's git worktree is created. Applies to new tabs.",
      wtRootInput,
    ),
  );

  // The setting that decides whether the feature works or is a trap: a fresh
  // worktree is a CLEAN checkout, so without .env / node_modules the agent's
  // first test run fails and it starts "fixing" a broken environment. Nested
  // paths allowed (src/web/node_modules) — plenty of repos don't keep deps at
  // the top level.
  seedsInput = document.createElement("textarea");
  seedsInput.className = "settings-control settings-control--text settings-control--area";
  seedsInput.rows = 4;
  seedsInput.spellcheck = false;
  worktrees.appendChild(
    makeWideRow(
      "Copy into new worktrees",
      "One per line. Files are copied; folders are linked. Any project can override this.",
      seedsInput,
    ),
  );

  // ── Sessions ────────────────────────────────────────────────────────────
  // The master switch the launch "Resume N Claude sessions?" prompt is gated by;
  // off means Perch never offers to reopen previous conversations on startup.
  resumeToggle = makeToggle("Resume Claude sessions on launch");
  sessions.appendChild(
    makeRow(
      "Resume Claude sessions on launch",
      "When Perch starts, offer to reopen the Claude conversations that were running.",
      resumeToggle,
    ),
  );

  // Where a new tab lands among its project's existing tabs. Top by default —
  // the tab you just made is the one you're about to use.
  newTabDropdown = new Dropdown();
  sessions.appendChild(
    makeRow(
      "New tab position",
      "Whether a new tab appears above or below its project's existing tabs.",
      newTabDropdown.element,
    ),
  );

  // ── About ───────────────────────────────────────────────────────────────
  const welcomeBtn = document.createElement("button");
  welcomeBtn.type = "button";
  welcomeBtn.className = "settings-btn settings-btn--subtle";
  welcomeBtn.textContent = "Show welcome";
  welcomeBtn.addEventListener("click", () => {
    closeSettings();
    showOnboarding();
  });
  about.appendChild(
    makeRow("Welcome screen", "Replay the quick getting-started tips.", welcomeBtn),
  );

  // The description doubles as the live status line (version + cadence, swapped
  // for the result after a manual check), so we keep refs to it and the button.
  updateCheckBtn = document.createElement("button");
  updateCheckBtn.type = "button";
  updateCheckBtn.className = "settings-btn settings-btn--subtle";
  updateCheckBtn.textContent = "Check now";
  updateCheckBtn.addEventListener("click", () => {
    if (!updateCheckBtn || !updateStatusEl) return;
    updateCheckBtn.disabled = true;
    updateCheckBtn.textContent = "Checking…";
    updateStatusEl.textContent = "Checking for updates…";
    send({ type: "update.check" });
  });
  const updatesRow = makeRow("Updates", UPDATE_CADENCE, updateCheckBtn);
  updateStatusEl = updatesRow.querySelector<HTMLElement>(".settings-row__desc");
  about.appendChild(updatesRow);

  // ── Footer: cancel + save, pinned ───────────────────────────────────────
  const footer = document.createElement("div");
  footer.className = "settings-page__footer";

  const cancel = document.createElement("button");
  cancel.type = "button";
  cancel.className = "settings-btn settings-btn--subtle";
  cancel.textContent = "Cancel";
  cancel.addEventListener("click", () => closeSettings());

  const ok = document.createElement("button");
  ok.type = "button";
  ok.className = "settings-btn settings-btn--accent";
  ok.textContent = "Save";
  ok.addEventListener("click", () => save());

  footer.append(cancel, ok);
  overlay.appendChild(footer);

  document.body.appendChild(overlay);
  document.addEventListener("keydown", onKeyDown, true);
  requestAnimationFrame(() => shellDropdown?.focus());
}

/** Build a Fluent on/off toggle (button[role=switch]). Click flips it; the
 *  caller reads/writes state via getToggle/setToggle (aria-checked is the
 *  source of truth, so the CSS [aria-checked] selectors drive the visuals). */
function makeToggle(ariaLabel: string): HTMLButtonElement {
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "settings-toggle";
  btn.setAttribute("role", "switch");
  btn.setAttribute("aria-label", ariaLabel);
  btn.setAttribute("aria-checked", "false");
  const knob = document.createElement("span");
  knob.className = "settings-toggle__knob";
  btn.appendChild(knob);
  btn.addEventListener("click", () => setToggle(btn, !getToggle(btn)));
  return btn;
}

function getToggle(btn: HTMLButtonElement): boolean {
  return btn.getAttribute("aria-checked") === "true";
}

function setToggle(btn: HTMLButtonElement, on: boolean): void {
  btn.setAttribute("aria-checked", on ? "true" : "false");
}

/** Build a CardControl-style row: text block left, control right. */
function makeRow(label: string, desc: string, control: HTMLElement): HTMLElement {
  const row = document.createElement("div");
  row.className = "settings-row";

  const text = document.createElement("div");
  text.className = "settings-row__text";

  const labelEl = document.createElement("div");
  labelEl.className = "settings-row__label";
  labelEl.textContent = label;

  const descEl = document.createElement("div");
  descEl.className = "settings-row__desc";
  descEl.textContent = desc;

  text.append(labelEl, descEl);
  row.append(text, control);
  return row;
}

/** A row whose control needs the full width — multi-line paths, the project
 *  list. Label and description sit ABOVE the control instead of beside it: a
 *  260px control column can't hold a list of file paths without clipping, which
 *  is exactly how the project list ended up with its Unregister button cut off. */
function makeWideRow(label: string, desc: string, control: HTMLElement): HTMLElement {
  const row = makeRow(label, desc, control);
  row.classList.add("settings-row--wide");
  return row;
}


function onKeyDown(ev: KeyboardEvent): void {
  if (!overlay) return;
  if (ev.key === "Escape") {
    ev.preventDefault();
    ev.stopPropagation();
    closeSettings();
  } else if (ev.key === "Enter" && (ev.ctrlKey || ev.metaKey)) {
    // Ctrl+Enter saves — a number/text input swallows plain Enter.
    ev.preventDefault();
    ev.stopPropagation();
    save();
  }
}
