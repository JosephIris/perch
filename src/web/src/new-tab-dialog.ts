// "New tab in <project>". Name → agent → worktree, then the host does the rest
// (cut the worktree, pick an unused color, spawn the agent with that name).
//
// The name is the only required field, and it does real work: it's the tab's
// title, it's slugified into the branch, and it's passed to `claude --name` so
// the session is labelled the same on the inside.

import { send, type ProjectView } from "./bridge.js";
import { MODEL_OPTIONS, modelLimitHint } from "./model-menu.js";
import { normalizeUrl } from "./browser-prompt.js";

let overlay: HTMLElement | null = null;

export function closeNewTabDialog() {
  overlay?.remove();
  overlay = null;
}

export function showNewTabDialog(project: ProjectView) {
  closeNewTabDialog();

  overlay = document.createElement("div");
  overlay.className = "projects-overlay";
  overlay.addEventListener("click", (e) => {
    if (e.target === overlay) closeNewTabDialog();
  });

  const card = document.createElement("div");
  card.className = "projects-card newtab-card";
  card.setAttribute("role", "dialog");
  card.setAttribute("aria-modal", "true");
  card.setAttribute("aria-label", `New tab in ${project.name}`);

  const h = document.createElement("h2");
  h.className = "projects-card__title";
  h.textContent = `New tab in ${project.name}`;
  card.appendChild(h);

  // ── name ────────────────────────────────────────────────────────────────
  const nameLabel = document.createElement("label");
  nameLabel.className = "newtab-field";
  const nameText = document.createElement("span");
  nameText.className = "newtab-field__label";
  nameText.textContent = "Name";
  nameLabel.appendChild(nameText);

  const nameInput = document.createElement("input");
  nameInput.type = "text";
  nameInput.className = "settings-control settings-control--text newtab-input";
  nameInput.placeholder = "fix the loc diff";
  nameInput.spellcheck = false;
  nameInput.autocomplete = "off";
  nameLabel.appendChild(nameInput);
  card.appendChild(nameLabel);

  // ── agent ───────────────────────────────────────────────────────────────
  const agentField = document.createElement("div");
  agentField.className = "newtab-field";
  const agentText = document.createElement("span");
  agentText.className = "newtab-field__label";
  agentText.textContent = "Start with";
  agentField.appendChild(agentText);

  type Agent = "claude" | "codex" | "shell" | "browser";
  const agents: { id: Agent; label: string }[] = [
    { id: "claude", label: "Claude" },
    { id: "codex", label: "Codex" },
    { id: "shell", label: "Shell" },
    { id: "browser", label: "Browser" },
  ];
  let agent: Agent = "claude";

  const seg = document.createElement("div");
  seg.className = "newtab-seg";
  const buttons = agents.map((a) => {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "newtab-seg__btn";
    b.textContent = a.label;
    b.setAttribute("aria-pressed", String(a.id === agent));
    b.addEventListener("click", () => {
      agent = a.id;
      for (const other of buttons) other.setAttribute("aria-pressed", "false");
      b.setAttribute("aria-pressed", "true");
      syncWorktreeHint();
      syncModelField();
      syncBrowserFields();
    });
    seg.appendChild(b);
    return b;
  });
  agentField.appendChild(seg);
  card.appendChild(agentField);

  // ── url (Browser only) ────────────────────────────────────────────────────
  // A browser tab's root is a webview, not a terminal — it needs an address
  // (our webview pane has no URL bar), and it doesn't take a model, worktree,
  // or a required name (the page auto-titles from the site <title>).
  const urlField = document.createElement("label");
  urlField.className = "newtab-field";
  const urlText = document.createElement("span");
  urlText.className = "newtab-field__label";
  urlText.textContent = "Address";
  urlField.appendChild(urlText);

  const urlInput = document.createElement("input");
  urlInput.type = "text";
  urlInput.className = "settings-control settings-control--text newtab-input";
  urlInput.placeholder = "example.com or localhost:3000";
  urlInput.spellcheck = false;
  urlInput.autocomplete = "off";
  urlField.appendChild(urlInput);
  card.appendChild(urlField);

  // ── model (Claude only) ─────────────────────────────────────────────────
  // Same segmented idiom as the agent picker; five short labels fit the card.
  // Hidden for codex/shell — but the selection is kept, so toggling back to
  // Claude restores it. Default preselected. A model at its usage limit is
  // disabled, with a quiet hint line naming it and its reset time.
  const modelField = document.createElement("div");
  modelField.className = "newtab-field";
  const modelText = document.createElement("span");
  modelText.className = "newtab-field__label";
  modelText.textContent = "Model";
  modelField.appendChild(modelText);

  let model = "";
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
  modelField.appendChild(modelSeg);

  if (limitNotes.length > 0) {
    const limitHint = document.createElement("span");
    limitHint.className = "newtab-field__hint";
    limitHint.textContent = limitNotes.join(" · ");
    modelField.appendChild(limitHint);
  }
  card.appendChild(modelField);

  const syncModelField = () => {
    modelField.style.display = agent === "claude" ? "" : "none";
  };
  syncModelField();

  // ── worktree ────────────────────────────────────────────────────────────
  // OFF by default. A worktree is a real folder on a new branch — the gentler
  // default is to open in the repo you already know you're in, and to let you
  // opt into isolation when you actually want a second agent running alongside.
  // (Termic defaults the same way, and calls it "the gentler default".)
  const wtField = document.createElement("label");
  wtField.className = "newtab-check";

  const wtCheck = document.createElement("input");
  wtCheck.type = "checkbox";
  wtCheck.className = "newtab-check__input";
  wtCheck.checked = false;
  wtField.appendChild(wtCheck);

  // Custom box: the native checkbox is an OS-painted control we can't theme, and
  // it looks like a stray Win32 widget on this surface. The real <input> stays
  // (it IS the state, and keeps keyboard + a11y for free) but is visually hidden
  // behind this; :checked / :focus-visible drive the drawing.
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
      ? "A separate folder on a new branch. Two agents can't overwrite each other's files, and this tab's change counts are its own."
      : `Opens in ${project.name} itself. Fine on its own — but tabs sharing the folder can overwrite each other's edits, and their change counts overlap.`;
    wtField.dataset.checked = String(wtCheck.checked);
  };
  wtCheck.addEventListener("change", syncWorktreeHint);
  syncWorktreeHint();

  // Browser tab: swap the terminal-oriented fields (worktree) for the address,
  // and make the name optional. Model is already claude-only via syncModelField.
  const syncBrowserFields = () => {
    const isBrowser = agent === "browser";
    urlField.style.display = isBrowser ? "" : "none";
    wtField.style.display = isBrowser ? "none" : "";
    nameText.textContent = isBrowser ? "Name (optional)" : "Name";
  };
  syncBrowserFields();

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
  cancel.addEventListener("click", () => closeNewTabDialog());
  actions.appendChild(cancel);

  const create = document.createElement("button");
  create.type = "button";
  create.className = "projects-card__btn projects-card__btn--primary";
  create.textContent = "Create tab";

  const submit = () => {
    // Browser tab: the address is required, the name isn't; no worktree/model.
    if (agent === "browser") {
      const url = normalizeUrl(urlInput.value);
      if (!url) {
        urlInput.focus();
        return;
      }
      send({
        type: "project.tab.new",
        projectId: project.id,
        name: nameInput.value.trim(),
        agent,
        worktree: false,
        url,
      });
      closeNewTabDialog();
      return;
    }
    const name = nameInput.value.trim();
    if (!name) {
      nameInput.focus();
      return;
    }
    send({
      type: "project.tab.new",
      projectId: project.id,
      name,
      agent,
      worktree: wtCheck.checked,
      // Only meaningful for Claude tabs; "" (Default) is omitted — absent and
      // "" read identically host-side (account default).
      ...(agent === "claude" && model ? { model } : {}),
    });
    closeNewTabDialog();
  };
  create.addEventListener("click", submit);
  actions.appendChild(create);
  card.appendChild(actions);

  nameInput.addEventListener("keydown", (e) => {
    if (e.key === "Enter") submit();
  });
  urlInput.addEventListener("keydown", (e) => {
    if (e.key === "Enter") submit();
    e.stopPropagation();
  });

  overlay.appendChild(card);
  document.body.appendChild(overlay);
  nameInput.focus();

  const esc = (e: KeyboardEvent) => {
    if (e.key !== "Escape" || !overlay) return;
    closeNewTabDialog();
    window.removeEventListener("keydown", esc);
  };
  window.addEventListener("keydown", esc);
}
