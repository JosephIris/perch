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
}

let openDialog = false;

export function confirmDialog(opts: ConfirmOpts): Promise<boolean> {
  // One confirm at a time — a second request resolves false rather than
  // stacking modals.
  if (openDialog) return Promise.resolve(false);
  openDialog = true;

  return new Promise<boolean>((resolve) => {
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
    card.append(title, body, footer);
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
      resolve(result);
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
