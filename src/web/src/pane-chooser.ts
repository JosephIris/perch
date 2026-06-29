// The new-pane chooser — a centered dialog shown INSIDE a freshly-split
// terminal pane whose source pane already has a known working directory (an
// agent ran there, OSC 7 reported the cwd). It offers three ways to start the
// pane — an agent in that dir, a plain shell in that dir, or a plain shell in
// the default dir — and resolves with the user's pick. The caller ships that
// pick to the host (which has parked the pane's shell spawn until we answer).
// Esc / backdrop / Cancel resolve "cancel" → the host closes the never-spawned
// pane (undoes the split).
//
// Unlike confirm.ts / settings.ts (full-window overlays appended to
// document.body), this overlay is scoped to ONE pane: it's absolutely
// positioned inside the pane element (which is position:relative;
// overflow:hidden in style.css), so it reads as "this pane is asking" and
// stays clipped to the pane's rounded card. Promise-based like confirmDialog,
// so it carries no bridge/IPC dependency — the caller decides what a pick means.

export type ChooserChoice = "agent" | "same" | "default" | "cancel";

export interface ChooserOpts {
  /** Source pane's working dir — where "agent" / "same" land, and the label. */
  cwd: string;
  /** "claude" / "codex" / "" — picks the agent button's label. */
  agentType: string;
  /** Configured default dir — where "default" lands. */
  defaultCwd: string;
}

/** Last path segment of a Windows/Unix path, for a friendly folder label.
 *  A drive root ("C:\") yields the drive ("C:"); a blank or all-separator
 *  path falls back to the input unchanged. Exported for unit tests. */
export function folderName(p: string): string {
  const trimmed = p.replace(/[\\/]+$/, "");
  const seg = trimmed.split(/[\\/]/).pop();
  return seg && seg.length ? seg : p;
}

/** Build + show the chooser inside `paneEl`. Resolves once with the user's
 *  pick ("agent" | "same" | "default" | "cancel"); the overlay then animates
 *  out and removes itself. */
export function showPaneChooser(paneEl: HTMLElement, opts: ChooserOpts): Promise<ChooserChoice> {
  return new Promise<ChooserChoice>((resolve) => {
    const agentName = opts.agentType === "codex" ? "Codex" : "Claude Code";
    const here = folderName(opts.cwd);
    const there = folderName(opts.defaultCwd);

    const overlay = document.createElement("div");
    overlay.className = "pane-chooser";

    const card = document.createElement("div");
    card.className = "pane-chooser__card";
    card.setAttribute("role", "dialog");
    card.setAttribute("aria-modal", "true");
    card.setAttribute("aria-label", "Set up new pane");

    const title = document.createElement("h2");
    title.className = "pane-chooser__title";
    title.textContent = "New pane";

    const sub = document.createElement("p");
    sub.className = "pane-chooser__sub";
    sub.textContent = opts.cwd;
    sub.title = opts.cwd;

    const options = document.createElement("div");
    options.className = "pane-chooser__options";

    // Build one option row: a left-aligned button with a title line, a secondary
    // desc line, and a number-key hint chip. `key` doubles as the 1/2/3 shortcut.
    const makeOption = (
      choice: Exclude<ChooserChoice, "cancel">,
      key: string,
      label: string,
      desc: string,
      primary: boolean,
    ): HTMLButtonElement => {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "pane-chooser__opt" + (primary ? " pane-chooser__opt--primary" : "");

      const text = document.createElement("span");
      text.className = "pane-chooser__opt-text";
      const t = document.createElement("span");
      t.className = "pane-chooser__opt-title";
      t.textContent = label;
      const d = document.createElement("span");
      d.className = "pane-chooser__opt-desc";
      d.textContent = desc;
      d.title = desc;
      text.append(t, d);

      const kbd = document.createElement("kbd");
      kbd.className = "pane-chooser__key";
      kbd.textContent = key;

      btn.append(text, kbd);
      btn.addEventListener("click", () => finish(choice));
      return btn;
    };

    const optAgent = makeOption("agent", "1", `Start ${agentName} here`, here, true);
    const optSame = makeOption("same", "2", "Open a shell here", here, false);
    const optDef = makeOption("default", "3", "Open a shell in the default folder", there, false);
    options.append(optAgent, optSame, optDef);

    const footer = document.createElement("div");
    footer.className = "pane-chooser__footer";
    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.className = "settings-btn settings-btn--subtle";
    cancel.textContent = "Cancel";
    cancel.addEventListener("click", () => finish("cancel"));
    footer.appendChild(cancel);

    card.append(title, sub, options, footer);
    overlay.appendChild(card);
    paneEl.appendChild(overlay);

    let settled = false;
    const finish = (choice: ChooserChoice) => {
      if (settled) return;
      settled = true;
      overlay.removeEventListener("keydown", onKeyDown, true);
      overlay.classList.add("pane-chooser--closing");
      overlay.addEventListener("animationend", () => overlay.remove(), { once: true });
      setTimeout(() => overlay.remove(), 260); // reduced-motion fallback
      resolve(choice);
    };

    function onKeyDown(ev: KeyboardEvent) {
      switch (ev.key) {
        case "Escape": ev.preventDefault(); ev.stopPropagation(); finish("cancel"); break;
        // Enter activates the focused option (button default) — but if focus has
        // drifted, treat it as the primary action so a blind Enter still works.
        case "Enter":
          if (!(ev.target instanceof HTMLButtonElement)) {
            ev.preventDefault(); ev.stopPropagation(); finish("agent");
          }
          break;
        case "1": ev.preventDefault(); ev.stopPropagation(); finish("agent"); break;
        case "2": ev.preventDefault(); ev.stopPropagation(); finish("same"); break;
        case "3": ev.preventDefault(); ev.stopPropagation(); finish("default"); break;
      }
    }

    // Backdrop (outside the card) cancels.
    overlay.addEventListener("mousedown", (ev) => {
      if (ev.target === overlay) finish("cancel");
    });
    // Capture keydown on the overlay so the number/Esc/Enter keys land here and
    // never reach xterm — the pane has no PTY yet, but its terminal still grabs
    // keystrokes. Focusing the primary option (below) keeps focus inside.
    overlay.addEventListener("keydown", onKeyDown, true);

    requestAnimationFrame(() => optAgent.focus({ preventScroll: true }));
  });
}
