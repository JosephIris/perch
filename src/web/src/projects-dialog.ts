// The "register a repo" picker. Opened from the project-mode empty state
// ("Find repos") and shown with whatever the host's scan turned up.
//
// Two groups, because they mean different things:
//   - "Open in a pane"  — repos you're literally working in right now. These are
//     pre-checked: if you're asking to find repos and you're already inside one,
//     it's almost certainly one you want.
//   - "Found in <root>" — the one-level scan of your configured roots. NOT
//     pre-checked; a folder existing under ~/src isn't evidence you want it.
//
// Nothing is registered until you confirm, and the dialog says exactly how many.

import { send, type ProjectsCandidatesMessage } from "./bridge.js";

let overlay: HTMLElement | null = null;

export function closeProjectsDialog() {
  overlay?.remove();
  overlay = null;
}

export function showProjectsDialog(msg: ProjectsCandidatesMessage) {
  closeProjectsDialog();

  const inUse = msg.candidates.filter((c) => c.source === "inUse");
  const scanned = msg.candidates.filter((c) => c.source === "scanned");
  const checked = new Set<string>(inUse.map((c) => c.path));

  overlay = document.createElement("div");
  overlay.className = "projects-overlay";
  overlay.addEventListener("click", (e) => {
    if (e.target === overlay) closeProjectsDialog();
  });

  const card = document.createElement("div");
  card.className = "projects-card";
  card.setAttribute("role", "dialog");
  card.setAttribute("aria-modal", "true");
  card.setAttribute("aria-label", "Add projects");

  const h = document.createElement("h2");
  h.className = "projects-card__title";
  h.textContent = "Add projects";
  card.appendChild(h);

  const body = document.createElement("div");
  body.className = "projects-card__body";

  const confirm = document.createElement("button");   // declared early: the row
  confirm.type = "button";                            // checkboxes update its label
  confirm.className = "projects-card__btn projects-card__btn--primary";

  const syncConfirm = () => {
    confirm.textContent = checked.size
      ? `Add ${checked.size} project${checked.size === 1 ? "" : "s"}`
      : "Add projects";
    confirm.disabled = checked.size === 0;
  };

  const group = (label: string, items: typeof msg.candidates) => {
    if (!items.length) return;
    const g = document.createElement("div");
    g.className = "projects-card__group";

    const gl = document.createElement("div");
    gl.className = "projects-card__group-label";
    gl.textContent = label;
    g.appendChild(gl);

    for (const c of items) {
      const row = document.createElement("label");
      row.className = "projects-row";

      const box = document.createElement("input");
      box.type = "checkbox";
      box.className = "projects-row__check";
      box.checked = checked.has(c.path);
      box.addEventListener("change", () => {
        if (box.checked) checked.add(c.path);
        else checked.delete(c.path);
        syncConfirm();
      });
      row.appendChild(box);

      const text = document.createElement("span");
      text.className = "projects-row__text";

      const name = document.createElement("span");
      name.className = "projects-row__name";
      name.textContent = c.name;
      text.appendChild(name);

      const path = document.createElement("span");
      path.className = "projects-row__path";
      path.textContent = c.path;
      text.appendChild(path);

      row.appendChild(text);
      g.appendChild(row);
    }
    body.appendChild(g);
  };

  group("Open in a pane", inUse);
  group(
    msg.scanRoots.length ? `Found in ${msg.scanRoots.join(", ")}` : "Found by scan",
    scanned
  );

  if (!msg.candidates.length) {
    const empty = document.createElement("div");
    empty.className = "projects-card__empty";
    empty.textContent = msg.scanRoots.length
      ? "No new repos found. Everything under your scan folders is already registered."
      : "No repos found. Add a scan folder in Settings, or pick one directly.";
    body.appendChild(empty);
  }

  card.appendChild(body);

  const actions = document.createElement("div");
  actions.className = "projects-card__actions";

  const browse = document.createElement("button");
  browse.type = "button";
  browse.className = "projects-card__btn";
  browse.textContent = "Add folder…";
  browse.addEventListener("click", () => {
    send({ type: "project.browse" });
    closeProjectsDialog();
  });
  actions.appendChild(browse);

  const spacer = document.createElement("div");
  spacer.className = "projects-card__spacer";
  actions.appendChild(spacer);

  const cancel = document.createElement("button");
  cancel.type = "button";
  cancel.className = "projects-card__btn";
  cancel.textContent = "Cancel";
  cancel.addEventListener("click", () => closeProjectsDialog());
  actions.appendChild(cancel);

  confirm.addEventListener("click", () => {
    // One project.add per pick. The host's Add() is idempotent by normalized
    // path, so a double-fire (or a repo that's somehow already registered) is
    // harmless rather than a duplicate row.
    for (const c of msg.candidates)
      if (checked.has(c.path)) send({ type: "project.add", path: c.path, name: c.name });
    closeProjectsDialog();
  });
  actions.appendChild(confirm);
  syncConfirm();

  card.appendChild(actions);
  overlay.appendChild(card);
  document.body.appendChild(overlay);

  const esc = (e: KeyboardEvent) => {
    if (e.key !== "Escape" || !overlay) return;
    closeProjectsDialog();
    window.removeEventListener("keydown", esc);
  };
  window.addEventListener("keydown", esc);
}
