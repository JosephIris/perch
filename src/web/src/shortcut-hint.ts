// Shortcut-hint overlay. Shows after the user holds Ctrl+Shift for a brief
// moment so quick chord input (Ctrl+Shift+T pressed and released) doesn't
// flash a card; longer holds reveal the available actions. Hides the
// moment either modifier is released or the window loses focus.
//
// The hint card is positioned + styled entirely in CSS; this file only
// flips the `.is-open` class.

const HOLD_MS = 300;

export function installShortcutHint(el: HTMLElement) {
  let timer: number | null = null;
  let open = false;

  function show() {
    if (open) return;
    open = true;
    el.classList.add("is-open");
  }
  function hide() {
    if (timer != null) { clearTimeout(timer); timer = null; }
    if (!open) return;
    open = false;
    el.classList.remove("is-open");
  }

  function isChord(ev: KeyboardEvent | { ctrlKey: boolean; shiftKey: boolean }) {
    return ev.ctrlKey && ev.shiftKey;
  }

  // We can't trust just keydown events (the user might already be holding
  // Ctrl+Shift when the window gains focus). So check on every keydown +
  // keyup, and let the timer arm/disarm based on the modifier pair.
  window.addEventListener("keydown", (ev) => {
    if (isChord(ev)) {
      if (open || timer != null) return;
      timer = window.setTimeout(show, HOLD_MS);
    } else {
      hide();
    }
  }, /* capture */ true);

  window.addEventListener("keyup", (ev) => {
    if (!isChord(ev)) hide();
  }, /* capture */ true);

  // Modifiers can stick if the window loses focus mid-chord (alt-tab,
  // task-switcher) -- bail out cleanly.
  window.addEventListener("blur", hide);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) hide();
  });
}
