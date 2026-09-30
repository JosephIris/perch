// While a project chat's board is on screen, the journal rail on the right
// steps aside so the board has the room. It is a mode, not your setting: the
// rail's open/closed preference is untouched and comes back as it was the
// moment the board is no longer showing (another tab, the Threads tab, the
// Overview hidden).

/** Re-check whether a board is showing; call after anything that could
 *  change it (the board opening or closing, a tab switch). */
export function refreshBoardMode(): void {
  const app = document.getElementById("app");
  if (!app) return;
  // A pane of a tab that isn't showing sits in a display:none container, so
  // it has no box: only a board you can see counts.
  const showing = [...document.querySelectorAll<HTMLElement>(".pane--chat-board:not(.pane--chat-noov)")]
    .some((p) => p.getClientRects().length > 0);
  app.classList.toggle("app--board", showing);
}
