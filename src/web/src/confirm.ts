// Lightweight confirmation dialog. A centered modal on the same surface as the
// settings dialog (Constitution: centering allowed for dialogs only). Returns a
// Promise that resolves true only if the user explicitly confirms; cancel,
// backdrop click, and Esc all resolve false. Enter confirms. Used to guard
// destructive, hard-to-undo actions — closing a session tears down its whole
// pane layout, which can't be recovered, so it's worth one deliberate click.

interface ConfirmOpts {
  title: string;
  body: string;
  confirmLabel: string;
  cancelLabel?: string;
  /** Render the confirm button as the red destructive variant. */
  danger?: boolean;
  /** An extra, opt-in destructive step folded into the same confirm (e.g. "also
   *  delete the worktree folder"). Defaults to UNCHECKED — an extra checkbox is
   *  never a thing to do by accident. */
  option?: { label: string; hint?: string };
}

export type ConfirmResult = { ok: boolean; optionChecked: boolean };

let openDialog = false;

/** Plain yes/no. */
export function confirmDialog(opts: ConfirmOpts): Promise<boolean> {
  return confirmWithOption(opts).then((r) => r.ok);
}

/** Yes/no plus an optional checkbox, so a single confirm can carry a second,
 *  opt-in destructive step instead of stacking two modals on the user. */
export function confirmWithOption(opts: ConfirmOpts): Promise<ConfirmResult> {
  // One confirm at a time — a second request resolves false rather than
  // stacking modals.
  if (openDialog) return Promise.resolve({ ok: false, optionChecked: false });
  openDialog = true;

  return new Promise<ConfirmResult>((resolve) => {
    const overlay = document.createElement("div");
    overlay.className = "settings-overlay";

    const card = document.createElement("div");
    card.className = "settings-card confirm-card";
    card.setAttribute("role", "alertdialog");
    card.setAttribute("aria-modal", "true");

    const title = document.createElement("h2");
    title.className = "settings-card__title";
    title.textContent = opts.title;

    const body = document.createElement("p");
    body.className = "confirm-card__body";
    body.textContent = opts.body;

    const footer = document.createElement("div");
    footer.className = "settings-card__footer";

    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.className = "settings-btn settings-btn--subtle";
    cancel.textContent = opts.cancelLabel ?? "Cancel";

    const confirm = document.createElement("button");
    confirm.type = "button";
    confirm.className =
      "settings-btn " + (opts.danger ? "settings-btn--danger" : "settings-btn--accent");
    confirm.textContent = opts.confirmLabel;

    footer.append(cancel, confirm);
    card.append(title, body);

    // Optional second step. Unchecked by default: it's the more destructive of
    // the two actions, and it must never ride along unnoticed.
    let optionBox: HTMLInputElement | null = null;
    if (opts.option) {
      const row = document.createElement("label");
      row.className = "confirm-card__option";

      optionBox = document.createElement("input");
      optionBox.type = "checkbox";
      optionBox.className = "projects-row__check";
      row.appendChild(optionBox);

      const text = document.createElement("span");
      text.className = "confirm-card__option-text";
      const label = document.createElement("span");
      label.textContent = opts.option.label;
      text.appendChild(label);
      if (opts.option.hint) {
        const hint = document.createElement("span");
        hint.className = "confirm-card__option-hint";
        hint.textContent = opts.option.hint;
        text.appendChild(hint);
      }
      row.appendChild(text);
      card.appendChild(row);
    }

    card.appendChild(footer);
    overlay.appendChild(card);
    document.body.appendChild(overlay);

    let settled = false;
    const finish = (result: boolean) => {
      if (settled) return;
      settled = true;
      openDialog = false;
      document.removeEventListener("keydown", onKeyDown, true);
      overlay.classList.add("settings-overlay--closing");
      overlay.addEventListener("animationend", () => overlay.remove(), { once: true });
      window.setTimeout(() => overlay.remove(), 260); // reduced-motion fallback
      resolve({ ok: result, optionChecked: result && !!optionBox?.checked });
    };

    function onKeyDown(ev: KeyboardEvent) {
      if (ev.key === "Escape") {
        ev.preventDefault();
        ev.stopPropagation();
        finish(false);
      } else if (ev.key === "Enter") {
        ev.preventDefault();
        ev.stopPropagation();
        finish(true);
      }
    }

    overlay.addEventListener("mousedown", (ev) => {
      if (ev.target === overlay) finish(false);
    });
    cancel.addEventListener("click", () => finish(false));
    confirm.addEventListener("click", () => finish(true));
    document.addEventListener("keydown", onKeyDown, true);

    // Focus the confirm button so Enter works and focus is trapped on the CTA.
    requestAnimationFrame(() => confirm.focus({ preventScroll: true }));
  });
}
