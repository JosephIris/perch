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

import { send } from "./bridge.js";
import type { SettingsDataMessage, InMessage } from "./bridge.js";
import { MIN_FONT_SIZE, MAX_FONT_SIZE, DEFAULT_FONT_SIZE } from "./pane.js";
import { Dropdown } from "./dropdown.js";
import { showOnboarding } from "./onboarding.js";

let overlay: HTMLElement | null = null;
let shellDropdown: Dropdown | null = null;
let cwdInput: HTMLInputElement | null = null;
let fontInput: HTMLInputElement | null = null;
let resumeToggle: HTMLButtonElement | null = null;
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
  overlay.classList.add("settings-overlay--closing");
  const el = overlay;
  overlay = null;
  shellDropdown?.dispose();
  shellDropdown = null;
  cwdInput = null;
  fontInput = null;
  resumeToggle = null;
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

  fontInput.value = String(msg.fontSize || DEFAULT_FONT_SIZE);

  // Default the toggle ON when the host omits the flag — matches the
  // Settings.ResumeAgentsOnLaunch code default (resume is opt-out).
  setToggle(resumeToggle, msg.resumeAgentsOnLaunch ?? true);

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
  });
  closeSettings();
}

function buildSkeleton(): void {
  overlay = document.createElement("div");
  overlay.className = "settings-overlay";
  // Click on the backdrop (not the card) dismisses.
  overlay.addEventListener("mousedown", (ev) => {
    if (ev.target === overlay) closeSettings();
  });

  const card = document.createElement("div");
  card.className = "settings-card";
  card.setAttribute("role", "dialog");
  card.setAttribute("aria-modal", "true");
  card.setAttribute("aria-label", "Settings");
  overlay.appendChild(card);

  const title = document.createElement("h2");
  title.className = "settings-card__title";
  title.textContent = "Settings";
  card.appendChild(title);

  const body = document.createElement("div");
  body.className = "settings-card__body";
  card.appendChild(body);

  // Row 1 — default shell. Custom dropdown (not native <select>) so the
  // option popup renders on our dark surface — WebView2 paints native
  // select popups with an OS-white background CSS can't reach.
  shellDropdown = new Dropdown();
  body.appendChild(
    makeRow(
      "Default shell",
      "Used for new sessions. Existing sessions keep their shell.",
      shellDropdown.element,
    ),
  );

  // Row 2 — default working directory.
  cwdInput = document.createElement("input");
  cwdInput.type = "text";
  cwdInput.className = "settings-control settings-control--text";
  cwdInput.spellcheck = false;
  cwdInput.autocomplete = "off";
  body.appendChild(
    makeRow(
      "Default working directory",
      "Where new sessions start when no directory is recorded.",
      cwdInput,
    ),
  );

  // Row 3 — default font size.
  fontInput = document.createElement("input");
  fontInput.type = "number";
  fontInput.min = String(MIN_FONT_SIZE);
  fontInput.max = String(MAX_FONT_SIZE);
  fontInput.step = "1";
  fontInput.className = "settings-control settings-control--number";
  body.appendChild(
    makeRow(
      "Terminal font size",
      `Pixels. Also adjustable with Ctrl + and Ctrl − (${MIN_FONT_SIZE}–${MAX_FONT_SIZE}).`,
      fontInput,
    ),
  );

  // Row 4 — resume Claude sessions on launch. This toggle is the master
  // switch the launch "Resume N Claude sessions?" prompt is gated by; off
  // means Perch never offers to reopen previous conversations on startup.
  resumeToggle = makeToggle("Resume Claude sessions on launch");
  body.appendChild(
    makeRow(
      "Resume Claude sessions on launch",
      "When Perch starts, offer to reopen the Claude conversations that were running.",
      resumeToggle,
    ),
  );

  // Row 5 — replay the onboarding lightbox. Closes settings first so the
  // welcome sits cleanly on the workspace, not stacked over the dialog.
  const welcomeBtn = document.createElement("button");
  welcomeBtn.type = "button";
  welcomeBtn.className = "settings-btn settings-btn--subtle";
  welcomeBtn.textContent = "Show welcome";
  welcomeBtn.addEventListener("click", () => {
    closeSettings();
    showOnboarding();
  });
  body.appendChild(
    makeRow(
      "Welcome screen",
      "Replay the quick getting-started tips.",
      welcomeBtn,
    ),
  );

  // Row 6 — updates. The description doubles as the live status line (version
  // + cadence, swapped for the result after a manual check), so we keep refs to
  // it and the button. applySettingsData fills the version; "Check now" runs the
  // same check the host does automatically, just on demand.
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
  body.appendChild(updatesRow);

  // Footer — cancel + save.
  const footer = document.createElement("div");
  footer.className = "settings-card__footer";

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
  card.appendChild(footer);

  document.body.appendChild(overlay);
  document.addEventListener("keydown", onKeyDown, true);
  // Focus the first control once attached.
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
