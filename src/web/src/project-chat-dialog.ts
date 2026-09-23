// "New project chat": the three things a project chat starts from — a name,
// an optional one-line goal (with one, Claude takes the first turn and
// proposes where to start) and optional instructions every thread is given.
// Same card family as the new-tab dialog.

import { send } from "./bridge.js";
import type { ProjectView } from "./bridge.js";

let overlay: HTMLElement | null = null;

export function closeProjectChatDialog() {
  overlay?.remove();
  overlay = null;
}

function field(label: string, hint: string, control: HTMLElement): HTMLElement {
  const f = document.createElement("label");
  f.className = "newtab-field";
  const t = document.createElement("span");
  t.className = "newtab-field__label";
  t.textContent = label;
  f.appendChild(t);
  f.appendChild(control);
  if (hint) {
    const h = document.createElement("span");
    h.className = "newtab-field__hint";
    h.textContent = hint;
    f.appendChild(h);
  }
  return f;
}

export function showProjectChatDialog(project: ProjectView) {
  closeProjectChatDialog();
  overlay = document.createElement("div");
  overlay.className = "projects-overlay";
  overlay.addEventListener("mousedown", (e) => { if (e.target === overlay) closeProjectChatDialog(); });

  const card = document.createElement("div");
  card.className = "projects-card newtab-card pchat-card";
  card.setAttribute("role", "dialog");
  card.setAttribute("aria-label", "New project chat");

  const h = document.createElement("div");
  h.className = "projects-card__title";
  h.textContent = `New project chat in ${project.name}`;
  card.appendChild(h);

  const intro = document.createElement("p");
  intro.className = "pchat-card__intro";
  intro.textContent =
    "One conversation that runs the work: you brief it, it hands tasks to threads — each a Claude in its own copy of the repo — and brings the results back.";
  card.appendChild(intro);

  const name = document.createElement("input");
  name.type = "text";
  name.className = "settings-control settings-control--text newtab-input";
  name.placeholder = `${project.name} chat`;
  name.spellcheck = false;
  card.appendChild(field("Name", "", name));

  const goal = document.createElement("input");
  goal.type = "text";
  goal.className = "settings-control settings-control--text newtab-input";
  goal.placeholder = "e.g. Ship the export feature by Friday";
  card.appendChild(field("Goal (optional)",
    "One line. With a goal, Claude looks at the project first and proposes where to start.", goal));

  const instructions = document.createElement("textarea");
  instructions.className = "settings-control settings-control--text settings-control--area newtab-input pchat-card__instructions";
  instructions.rows = 4;
  instructions.placeholder = "e.g. Work on the develop branch. Run the tests before reporting. Ask me before touching billing.";
  card.appendChild(field("Instructions (optional)",
    "Given to the chat and to every thread it starts. You can change them later.", instructions));

  const actions = document.createElement("div");
  actions.className = "projects-card__actions";
  const spacer = document.createElement("div");
  spacer.className = "projects-card__spacer";
  actions.appendChild(spacer);
  const cancel = document.createElement("button");
  cancel.type = "button";
  cancel.className = "projects-card__btn";
  cancel.textContent = "Cancel";
  cancel.addEventListener("click", () => closeProjectChatDialog());
  actions.appendChild(cancel);
  const create = document.createElement("button");
  create.type = "button";
  create.className = "projects-card__btn projects-card__btn--primary";
  create.textContent = "Create project chat";
  const submit = () => {
    send({
      type: "projectchat.new",
      id: project.id,
      name: name.value.trim(),
      goal: goal.value.trim(),
      instructions: instructions.value.trim(),
    });
    closeProjectChatDialog();
  };
  create.addEventListener("click", submit);
  actions.appendChild(create);
  card.appendChild(actions);

  for (const input of [name, goal]) input.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Enter") submit(); });
  instructions.addEventListener("keydown", (e) => e.stopPropagation());

  overlay.appendChild(card);
  document.body.appendChild(overlay);
  goal.focus();

  const esc = (e: KeyboardEvent) => {
    if (e.key !== "Escape" || !overlay) return;
    closeProjectChatDialog();
    window.removeEventListener("keydown", esc);
  };
  window.addEventListener("keydown", esc);
}
