// "New bot in <project>". One card, three stacked stages — the fields, then
// (after "Generate brief") a progress line, then the brief itself, editable.
//
// Not a wizard on purpose: the brief is written FROM the purpose, and the
// moment you are reading the brief is exactly when you want the purpose in
// view to compare against. Change the purpose after a generation and the
// button offers to regenerate.
//
// The host does the expensive part (a headless Claude run that reads the
// reference folder); this dialog only mints the jobId, shows progress, and
// hands the accepted text back inside team.bot.create. A reply for a job
// this dialog no longer owns is dropped, so a closed dialog can't resurrect.
//
// The same card, reduced to its brief stage, is the "Edit brief…" editor.

import {
  send,
  type ProjectView, type TeamPositionView,
  type TeamBriefProgressMessage, type TeamBriefResultMessage, type TeamReferencePickedMessage,
} from "./bridge.js";
import { MODEL_OPTIONS, modelLimitHint } from "./model-menu.js";
import { validateNickname } from "./mention.js";
import { spinnerSpan } from "./spinner.js";
import { elapsedSpan } from "./elapsed.js";
import { Dropdown } from "./dropdown.js";

let overlay: HTMLElement | null = null;
let currentJobId: string | null = null;
let currentRequestId: string | null = null;
let onProgress: ((m: TeamBriefProgressMessage) => void) | null = null;
let onResult: ((m: TeamBriefResultMessage) => void) | null = null;
let onPicked: ((m: TeamReferencePickedMessage) => void) | null = null;
let restoreFocus: HTMLElement | null = null;

export type NewBotMode = "new" | "existing";

/** Whether "Create bot" may be pressed. Pure: the whole enable decision, pinned
 *  by test so the button can't quietly allow a bot with no brief. */
export function canCreate(a: {
  mode: NewBotMode;
  nicknameError: string | null;
  positionName: string;
  purpose: string;
  referencePath: string;
  brief: string;
  positionSlug: string | null;
  generating: boolean;
}): boolean {
  if (a.nicknameError !== null || a.generating) return false;
  if (a.mode === "existing") return a.positionSlug !== null && a.positionSlug.length > 0;
  return a.positionName.trim().length > 0
    && a.purpose.trim().length > 0
    && a.referencePath.trim().length > 0
    && a.brief.trim().length > 0;
}

/** Whether "Generate brief" may be pressed. */
export function canGenerate(a: {
  mode: NewBotMode; positionName: string; purpose: string; referencePath: string; generating: boolean;
}): boolean {
  if (a.mode !== "new" || a.generating) return false;
  return a.positionName.trim().length > 0 && a.purpose.trim().length > 0 && a.referencePath.trim().length > 0;
}

function newId(): string {
  try { return crypto.randomUUID(); } catch { /* insecure context */ }
  return `j-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

export function closeNewBotDialog(): void {
  if (currentJobId) {
    send({ type: "team.brief.cancel", jobId: currentJobId });
    currentJobId = null;
  }
  currentRequestId = null;
  onProgress = null;
  onResult = null;
  onPicked = null;
  overlay?.remove();
  overlay = null;
  window.removeEventListener("keydown", onEsc, true);
  restoreFocus?.focus?.();
  restoreFocus = null;
}

/* Host replies, routed from main.ts. Ignored unless they name the job/request
 * this dialog currently owns. */
export function applyBriefProgress(msg: TeamBriefProgressMessage): void {
  if (msg.jobId === currentJobId) onProgress?.(msg);
}
export function applyBriefResult(msg: TeamBriefResultMessage): void {
  if (msg.jobId === currentJobId) onResult?.(msg);
}
export function applyReferencePicked(msg: TeamReferencePickedMessage): void {
  if (msg.requestId === currentRequestId) onPicked?.(msg);
}

function onEsc(e: KeyboardEvent): void {
  if (e.key !== "Escape" || !overlay) return;
  // A flyout on top (the model menu, a dropdown) owns Esc first.
  if (document.querySelector(".model-menu, .dropdown__menu, .mention-pop")) return;
  e.stopPropagation();
  closeNewBotDialog();
}

function field(labelText: string, control: HTMLElement, hint?: string, useLabel = true): HTMLElement {
  const wrap = document.createElement(useLabel ? "label" : "div");
  wrap.className = "newtab-field";
  const l = document.createElement("span");
  l.className = "newtab-field__label";
  l.textContent = labelText;
  wrap.appendChild(l);
  wrap.appendChild(control);
  if (hint !== undefined) {
    const h = document.createElement("span");
    h.className = "newtab-field__hint";
    h.textContent = hint;
    wrap.appendChild(h);
  }
  return wrap;
}

function textInput(placeholder: string, mono = false): HTMLInputElement {
  const i = document.createElement("input");
  i.type = "text";
  i.className = "settings-control settings-control--text newtab-input" + (mono ? "" : " newbot-input--prose");
  i.placeholder = placeholder;
  i.spellcheck = false;
  i.autocomplete = "off";
  return i;
}

function textArea(placeholder: string, rows: number): HTMLTextAreaElement {
  const t = document.createElement("textarea");
  t.className = "settings-control settings-control--area newtab-input newbot-area";
  t.placeholder = placeholder;
  t.rows = rows;
  t.spellcheck = true;
  return t;
}

function trapTab(card: HTMLElement, ev: KeyboardEvent): void {
  if (ev.key !== "Tab") return;
  const focusables = Array.from(card.querySelectorAll<HTMLElement>(
    "input:not([disabled]):not([type=hidden]), textarea:not([disabled]), button:not([disabled]), [tabindex]:not([tabindex='-1'])",
  )).filter((e) => e.offsetParent !== null || e === document.activeElement);
  if (focusables.length === 0) return;
  const first = focusables[0], last = focusables[focusables.length - 1];
  if (ev.shiftKey && document.activeElement === first) { ev.preventDefault(); last.focus(); }
  else if (!ev.shiftKey && document.activeElement === last) { ev.preventDefault(); first.focus(); }
}

function mountCard(title: string): HTMLElement {
  closeNewBotDialog();
  restoreFocus = document.activeElement as HTMLElement | null;

  overlay = document.createElement("div");
  overlay.className = "projects-overlay";
  overlay.addEventListener("click", (e) => { if (e.target === overlay) closeNewBotDialog(); });

  const card = document.createElement("div");
  card.className = "projects-card newbot-card";
  card.setAttribute("role", "dialog");
  card.setAttribute("aria-modal", "true");
  const titleId = `newbot-title-${Date.now().toString(36)}`;
  card.setAttribute("aria-labelledby", titleId);
  card.addEventListener("keydown", (ev) => {
    trapTab(card, ev);
    // Keystrokes inside the card must not reach xterm / the global chords.
    if (!ev.ctrlKey && !ev.altKey && !ev.metaKey) ev.stopPropagation();
  });

  const h = document.createElement("h2");
  h.className = "projects-card__title";
  h.id = titleId;
  h.textContent = title;
  card.appendChild(h);

  overlay.appendChild(card);
  document.body.appendChild(overlay);
  window.addEventListener("keydown", onEsc, true);
  return card;
}

/** The brief stage: progress line, error line, and the editable brief. Shared
 *  by the new-bot card and the brief editor. */
function briefStage(project: ProjectView, initial: string) {
  const stage = document.createElement("div");
  stage.className = "newbot-stage";
  stage.hidden = true;

  const progress = document.createElement("div");
  progress.className = "newbot-stage__progress";
  progress.setAttribute("role", "status");
  progress.setAttribute("aria-live", "polite");
  progress.hidden = true;
  stage.appendChild(progress);

  const error = document.createElement("div");
  error.className = "newbot-stage__error";
  error.setAttribute("role", "alert");
  error.hidden = true;
  stage.appendChild(error);

  const brief = textArea("## Role\n\nWhat this position owns, what it never touches, who it asks…", 12);
  brief.value = initial;
  brief.classList.add("newbot-brief");
  const briefField = field("Brief", brief, "Edit freely — this is what the bot reads at the start of every session.");
  briefField.hidden = initial.trim().length === 0;
  stage.appendChild(briefField);

  let startedAt = 0;
  const showProgress = (phase: string) => {
    stage.hidden = false;
    error.hidden = true;
    progress.hidden = false;
    progress.replaceChildren();
    progress.appendChild(spinnerSpan("newbot-stage__spinner"));
    const p = document.createElement("span");
    p.className = "newbot-stage__phase";
    p.textContent = phase;
    progress.appendChild(p);
    if (startedAt === 0) startedAt = Date.now();
    const t = elapsedSpan(startedAt, true);
    t.className = "newbot-stage__elapsed";
    progress.appendChild(t);
    const hint = document.createElement("span");
    hint.className = "newbot-stage__hint";
    hint.textContent = `Claude is reading ${project.name} — usually a minute or two.`;
    progress.appendChild(hint);
  };
  const showError = (message: string, onRetry: () => void, onWrite: () => void) => {
    stage.hidden = false;
    progress.hidden = true;
    startedAt = 0;
    error.hidden = false;
    error.replaceChildren();
    const text = document.createElement("span");
    text.className = "newbot-stage__error-text";
    text.textContent = `Couldn't write the brief: ${message}`;
    error.appendChild(text);
    const actions = document.createElement("span");
    actions.className = "newbot-stage__error-actions";
    const retry = document.createElement("button");
    retry.type = "button";
    retry.className = "projects-card__btn";
    retry.textContent = "Try again";
    retry.addEventListener("click", onRetry);
    const write = document.createElement("button");
    write.type = "button";
    write.className = "projects-card__btn";
    write.textContent = "Write it myself";
    write.addEventListener("click", onWrite);
    actions.append(retry, write);
    error.appendChild(actions);
  };
  const showBrief = (text: string) => {
    stage.hidden = false;
    progress.hidden = true;
    error.hidden = true;
    startedAt = 0;
    brief.value = text;
    briefField.hidden = false;
  };
  const reset = () => { startedAt = 0; progress.hidden = true; error.hidden = true; };

  return { stage, brief, showProgress, showError, showBrief, reset };
}

export function showNewBotDialog(project: ProjectView, opts?: { positionSlug?: string }): void {
  const card = mountCard(`New bot in ${project.name}`);
  const positions = project.team?.positions ?? [];
  const taken = (project.team?.bots ?? []).map((b) => b.nickname);

  // Opened for a specific position ("add another Frontend dev") → the existing
  // path; otherwise a new position, which is the only path when none exist.
  let mode: NewBotMode = positions.length > 0 && opts?.positionSlug ? "existing" : "new";
  const positionSlug: string | null = opts?.positionSlug ?? (positions[0]?.slug ?? null);
  let model = "";
  let generating = false;
  let generatedFor = "";   // purpose text the current brief was generated from

  // ── mode ────────────────────────────────────────────────────────────────
  let modeButtons: HTMLButtonElement[] = [];
  if (positions.length > 0) {
    const seg = document.createElement("div");
    seg.className = "newtab-seg";
    const options: { id: NewBotMode; label: string }[] = [
      { id: "new", label: "New position" },
      { id: "existing", label: "Existing position" },
    ];
    modeButtons = options.map((o) => {
      const b = document.createElement("button");
      b.type = "button";
      b.className = "newtab-seg__btn";
      b.textContent = o.label;
      b.setAttribute("aria-pressed", String(o.id === mode));
      b.addEventListener("click", () => {
        mode = o.id;
        for (const other of modeButtons) other.setAttribute("aria-pressed", "false");
        b.setAttribute("aria-pressed", "true");
        syncMode();
      });
      seg.appendChild(b);
      return b;
    });
    card.appendChild(field("Position", seg, undefined, false));
  }

  // ── nickname ────────────────────────────────────────────────────────────
  const nick = textInput("Ada");
  nick.maxLength = 24;
  const nickField = field("Nickname", nick, "How you'll @mention it in the room. Two bots can hold the same position under different nicknames.");
  const nickHint = nickField.querySelector<HTMLElement>(".newtab-field__hint")!;
  const nickHintText = nickHint.textContent!;
  card.appendChild(nickField);

  // ── new position fields ─────────────────────────────────────────────────
  const posName = textInput("Frontend dev");
  const posNameField = field("Position name", posName);
  card.appendChild(posNameField);

  const purpose = textArea("Owns everything under src/web — the sidebar, the panes, the dialogs. Keeps the chrome calm and on the design tokens.", 3);
  const purposeField = field("Purpose", purpose, "Plain language. Claude reads the reference folder and turns this into a standing brief.");
  card.appendChild(purposeField);

  const refWrap = document.createElement("div");
  refWrap.className = "newbot-path";
  const refInput = textInput(project.path, true);
  refInput.value = project.path;
  refInput.setAttribute("aria-label", "Reference folder");
  const browse = document.createElement("button");
  browse.type = "button";
  browse.className = "projects-card__btn newbot-path__browse";
  browse.textContent = "Browse…";
  browse.addEventListener("click", () => {
    currentRequestId = newId();
    onPicked = (m) => { if (m.path) { refInput.value = m.path; sync(); } };
    send({ type: "team.reference.browse", requestId: currentRequestId, projectId: project.id });
  });
  refWrap.append(refInput, browse);
  const refField = field("Reference folder", refWrap, "What Claude reads to write the brief. The project itself, unless this position is about another repo.", false);
  card.appendChild(refField);

  const modelSeg = document.createElement("div");
  modelSeg.className = "newtab-seg";
  const limitNotes: string[] = [];
  const modelButtons = MODEL_OPTIONS.map((m) => {
    const limit = modelLimitHint(m.alias);
    const b = document.createElement("button");
    b.type = "button";
    b.className = "newtab-seg__btn";
    b.textContent = m.label;
    b.setAttribute("aria-pressed", String(m.alias === model));
    if (limit !== null) {
      b.disabled = true;
      b.setAttribute("aria-disabled", "true");
      limitNotes.push(`${m.label} — ${limit}`);
    } else {
      b.addEventListener("click", () => {
        model = m.alias;
        for (const other of modelButtons) other.setAttribute("aria-pressed", "false");
        b.setAttribute("aria-pressed", "true");
      });
    }
    modelSeg.appendChild(b);
    return b;
  });
  const modelField = field("Model", modelSeg, limitNotes.length > 0 ? limitNotes.join(" · ") : undefined, false);
  card.appendChild(modelField);

  // ── existing position ───────────────────────────────────────────────────
  let posDropdown: Dropdown | null = null;
  let posDropdownField: HTMLElement | null = null;
  if (positions.length > 0) {
    posDropdown = new Dropdown();
    posDropdown.setOptions(
      positions.map((p) => ({ value: p.slug, label: p.model ? `${p.name} · ${p.model}` : p.name })),
      positionSlug ?? positions[0].slug,
    );
    posDropdown.element.classList.add("newbot-dropdown");
    // The dropdown has no change callback (settings reads .value at save);
    // re-sync on any click inside it so Create enables the moment a pick lands.
    posDropdown.element.addEventListener("click", () => setTimeout(sync, 0));
    posDropdownField = field("Position", posDropdown.element, "The new bot shares this position's brief.", false);
    card.appendChild(posDropdownField);
  }

  // ── worktree ────────────────────────────────────────────────────────────
  const wtField = document.createElement("label");
  wtField.className = "newtab-check";
  const wtCheck = document.createElement("input");
  wtCheck.type = "checkbox";
  wtCheck.className = "newtab-check__input";
  wtCheck.checked = true;
  wtField.appendChild(wtCheck);
  const wtBox = document.createElement("span");
  wtBox.className = "newtab-check__box";
  wtBox.setAttribute("aria-hidden", "true");
  wtBox.innerHTML =
    '<svg viewBox="0 0 12 12" width="12" height="12" fill="none">' +
    '<path d="M2.5 6.2 L4.8 8.5 L9.5 3.5" stroke="currentColor" stroke-width="1.6" ' +
    'stroke-linecap="round" stroke-linejoin="round"/></svg>';
  wtField.appendChild(wtBox);
  const wtText = document.createElement("span");
  wtText.className = "newtab-check__text";
  const wtTitle = document.createElement("span");
  wtTitle.className = "newtab-check__label";
  wtTitle.textContent = "Run in its own git worktree";
  wtText.appendChild(wtTitle);
  const wtHint = document.createElement("span");
  wtHint.className = "newtab-check__hint";
  wtText.appendChild(wtHint);
  wtField.appendChild(wtText);
  card.appendChild(wtField);
  const syncWorktreeHint = () => {
    wtHint.textContent = wtCheck.checked
      ? "A separate folder on its own branch, so teammates can't overwrite each other's files. Merging their work is a job for later."
      : `Works in ${project.name} itself. Two bots in one folder can overwrite each other's edits.`;
    wtField.dataset.checked = String(wtCheck.checked);
  };
  wtCheck.addEventListener("change", syncWorktreeHint);
  syncWorktreeHint();

  // ── brief stage ─────────────────────────────────────────────────────────
  const stage = briefStage(project, "");
  card.appendChild(stage.stage);

  // ── actions ─────────────────────────────────────────────────────────────
  const actions = document.createElement("div");
  actions.className = "projects-card__actions";
  const spacer = document.createElement("div");
  spacer.className = "projects-card__spacer";
  actions.appendChild(spacer);

  const cancel = document.createElement("button");
  cancel.type = "button";
  cancel.className = "projects-card__btn";
  cancel.textContent = "Cancel";
  actions.appendChild(cancel);

  const generate = document.createElement("button");
  generate.type = "button";
  generate.className = "projects-card__btn";
  generate.textContent = "Generate brief";
  actions.appendChild(generate);

  const create = document.createElement("button");
  create.type = "button";
  create.className = "projects-card__btn projects-card__btn--primary";
  create.textContent = "Create bot";
  actions.appendChild(create);
  card.appendChild(actions);

  const chosenSlug = () => (posDropdown ? posDropdown.value || positionSlug : positionSlug);

  const state = () => ({
    mode,
    nicknameError: validateNickname(nick.value, taken),
    positionName: posName.value,
    purpose: purpose.value,
    referencePath: refInput.value,
    brief: stage.brief.value,
    positionSlug: chosenSlug(),
    generating,
  });

  const sync = () => {
    const s = state();
    const err = s.nicknameError;
    const touched = nick.value.length > 0;
    nickHint.textContent = touched && err ? err : nickHintText;
    nickHint.classList.toggle("newtab-field__hint--error", touched && err !== null);
    generate.disabled = !canGenerate(s);
    // Once a brief exists the same button regenerates; a changed purpose is
    // the usual reason to, so say so in the hint rather than a second button.
    const hasBrief = stage.brief.value.trim().length > 0;
    generate.textContent = hasBrief ? "Regenerate" : "Generate brief";
    generate.title = hasBrief && generatedFor !== "" && generatedFor !== purpose.value.trim()
      ? "The purpose changed since this brief was written"
      : "";
    create.disabled = !canCreate(s);
    cancel.textContent = generating ? "Stop" : "Cancel";
  };

  const syncMode = () => {
    const isNew = mode === "new";
    posNameField.hidden = !isNew;
    purposeField.hidden = !isNew;
    refField.hidden = !isNew;
    modelField.hidden = !isNew;
    stage.stage.hidden = !isNew || (stage.brief.value.trim().length === 0 && stage.stage.hidden);
    generate.hidden = !isNew;
    if (posDropdownField) posDropdownField.hidden = isNew;
    sync();
  };

  const startGeneration = () => {
    if (!canGenerate(state())) return;
    generating = true;
    generatedFor = purpose.value.trim();
    currentJobId = newId();
    const jobId = currentJobId;
    stage.showProgress("Starting…");
    onProgress = (m) => stage.showProgress(m.phase || "Reading the repo…");
    onResult = (m) => {
      if (currentJobId !== jobId) return;
      currentJobId = null;
      generating = false;
      if (m.brief && m.brief.trim().length > 0) {
        stage.showBrief(m.brief.trim());
        stage.brief.focus({ preventScroll: true });
      } else {
        stage.showError(
          m.error ?? "no text came back",
          () => startGeneration(),
          () => {
            stage.showBrief(`## Role\n${purpose.value.trim()}\n\n## What you own\n\n## What you never touch\n\n## Who you ask\n\n## Definition of done\n\n## How you communicate on the team\n`);
            stage.brief.focus({ preventScroll: true });
          },
        );
      }
      sync();
    };
    send({
      type: "team.brief.generate",
      jobId,
      projectId: project.id,
      positionName: posName.value.trim(),
      purpose: purpose.value.trim(),
      referencePath: refInput.value.trim(),
      model,
    });
    sync();
  };

  generate.addEventListener("click", startGeneration);
  // Two-state cancel: "Stop" while a job runs cancels the job and keeps the
  // dialog (your fields are still there); "Cancel" otherwise closes it.
  cancel.addEventListener("click", () => {
    if (generating && currentJobId) {
      send({ type: "team.brief.cancel", jobId: currentJobId });
      currentJobId = null;
      generating = false;
      stage.reset();
      if (stage.brief.value.trim().length === 0) stage.stage.hidden = true;
      sync();
      return;
    }
    closeNewBotDialog();
  });

  const submit = () => {
    const s = state();
    if (!canCreate(s)) {
      if (s.nicknameError) nick.focus();
      return;
    }
    const slug = chosenSlug();
    if (mode === "existing" && slug) {
      send({ type: "team.bot.create", projectId: project.id, nickname: nick.value.trim(), worktree: wtCheck.checked, positionSlug: slug });
    } else {
      send({
        type: "team.bot.create",
        projectId: project.id,
        nickname: nick.value.trim(),
        worktree: wtCheck.checked,
        position: {
          name: posName.value.trim(),
          purpose: purpose.value.trim(),
          referencePath: refInput.value.trim(),
          model,
          brief: stage.brief.value.trim(),
        },
      });
    }
    currentJobId = null;   // accepted — nothing to cancel on close
    closeNewBotDialog();
  };
  create.addEventListener("click", submit);

  for (const i of [nick, posName, refInput]) {
    i.addEventListener("input", sync);
    i.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); if (!create.disabled) submit(); } });
  }
  purpose.addEventListener("input", sync);
  stage.brief.addEventListener("input", sync);

  syncMode();
  nick.focus();
}

/** "Edit brief…": the same card reduced to the position's purpose and brief.
 *  Save sends team.position.update; Regenerate runs the headless job again. */
export function showBriefEditor(project: ProjectView, position: TeamPositionView): void {
  const card = mountCard(`${position.name} — brief`);
  let generating = false;

  const purpose = textArea("", 3);
  purpose.value = position.purpose;
  card.appendChild(field("Purpose", purpose, "Plain language. Regenerating writes a fresh brief from this."));

  const stage = briefStage(project, position.brief ?? "");
  stage.stage.hidden = false;
  if ((position.brief ?? "").trim().length === 0) {
    const note = document.createElement("div");
    note.className = "newbot-stage__note";
    note.textContent = position.hasBrief
      ? "The brief's text isn't loaded here — regenerate to write a new one, or paste it below."
      : "No brief yet. Regenerate to write one from the purpose, or write it below.";
    stage.stage.prepend(note);
    stage.brief.parentElement!.hidden = false;
  }
  card.appendChild(stage.stage);

  const actions = document.createElement("div");
  actions.className = "projects-card__actions";
  const spacer = document.createElement("div");
  spacer.className = "projects-card__spacer";
  actions.appendChild(spacer);

  const cancel = document.createElement("button");
  cancel.type = "button";
  cancel.className = "projects-card__btn";
  cancel.textContent = "Cancel";
  cancel.addEventListener("click", () => {
    if (generating && currentJobId) {
      send({ type: "team.brief.cancel", jobId: currentJobId });
      currentJobId = null;
      generating = false;
      stage.reset();
      sync();
      return;
    }
    closeNewBotDialog();
  });
  actions.appendChild(cancel);

  const regen = document.createElement("button");
  regen.type = "button";
  regen.className = "projects-card__btn";
  regen.textContent = "Regenerate";
  actions.appendChild(regen);

  const save = document.createElement("button");
  save.type = "button";
  save.className = "projects-card__btn projects-card__btn--primary";
  save.textContent = "Save brief";
  actions.appendChild(save);
  card.appendChild(actions);

  const sync = () => {
    regen.disabled = generating || purpose.value.trim().length === 0;
    save.disabled = generating || stage.brief.value.trim().length === 0;
    cancel.textContent = generating ? "Stop" : "Cancel";
  };

  const startGeneration = () => {
    generating = true;
    currentJobId = newId();
    const jobId = currentJobId;
    stage.showProgress("Starting…");
    onProgress = (m) => stage.showProgress(m.phase || "Reading the repo…");
    onResult = (m) => {
      if (currentJobId !== jobId) return;
      currentJobId = null;
      generating = false;
      if (m.brief && m.brief.trim().length > 0) stage.showBrief(m.brief.trim());
      else stage.showError(m.error ?? "no text came back", startGeneration, () => stage.showBrief(stage.brief.value));
      sync();
    };
    send({
      type: "team.brief.generate",
      jobId,
      projectId: project.id,
      positionName: position.name,
      purpose: purpose.value.trim(),
      referencePath: project.path,
      model: position.model,
    });
    sync();
  };
  regen.addEventListener("click", startGeneration);

  save.addEventListener("click", () => {
    if (save.disabled) return;
    const msg: { type: "team.position.update"; projectId: string; slug: string; brief: string; purpose?: string } = {
      type: "team.position.update",
      projectId: project.id,
      slug: position.slug,
      brief: stage.brief.value.trim(),
    };
    if (purpose.value.trim() !== position.purpose) msg.purpose = purpose.value.trim();
    send(msg);
    currentJobId = null;
    closeNewBotDialog();
  });

  purpose.addEventListener("input", sync);
  stage.brief.addEventListener("input", sync);
  sync();
  stage.brief.focus();
}
