// While a project chat is on screen, the journal rail on the right steps
// aside: the chat shows each thread's steps itself, so the rail would only
// repeat them, and its room goes to the threads panel instead. It is a mode,
// not your setting: the rail's open/closed preference is untouched and comes
// back as it was the moment no chat is showing (another tab).

/** Re-check whether a chat is showing; call after anything that could
 *  change it (a tab switch, the Overview opening or closing). */
export function refreshBoardMode(): void {
  const app = document.getElementById("app");
  if (!app) return;
  // A pane of a tab that isn't showing sits in a display:none container, so
  // it has no box: only a chat you can see counts.
  const showing = [...document.querySelectorAll<HTMLElement>(".pane--chat")]
    .some((p) => p.getClientRects().length > 0);
  app.classList.toggle("app--board", showing);
}
