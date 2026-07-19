// Native airspace fix for web/URL panes.
//
// Each browser (URL) pane is a real WebView2 child HWND that the host has to
// SetWindowPos(HWND_TOP) above the main WebView2 just to render at all (see
// UrlPaneHost.cs). Native child windows always composite ABOVE the host's HTML,
// so a DOM modal — the new-tab dialog, settings, the cloud/local panels — can't
// paint over a web pane: it renders BEHIND the pane and gets clipped. That's the
// classic airspace problem, and there is no z-index cure from the HTML side.
//
// Pragmatic fix: while any full-viewport modal is on screen, ask the host to
// hide every web pane (SetVisible(false) → the pane shows its dark placeholder,
// which the modal's scrim covers anyway). Restore them when the last modal
// leaves.
//
// Driven off DOM presence via a MutationObserver rather than enter/exit calls
// wired into each modal: a modal that never "exits" — early return, thrown
// error, removed by a parent — can't strand the panes hidden, because the
// observer only ever reflects what's actually mounted right now. It also
// auto-covers any future modal that uses one of these overlay classes.

import { send } from "./bridge.js";

// Full-screen scrim modals that must sit above web panes. Every one is appended
// as a direct child of <body> (hence childList without subtree is enough) and
// removed on close. Menus / dropdowns / tooltips are deliberately excluded:
// they're small, short-lived, and usually nowhere near a web pane, so blanking
// every pane on each dropdown open would be a worse flicker than the rare
// occlusion. Add a class here if a new full-viewport modal family appears.
const MODAL_SELECTOR =
  ".projects-overlay,.settings-overlay,.cloud-overlay,.local-overlay,.onboarding-overlay";

let suppressed = false;

function sync(): void {
  const want = document.querySelector(MODAL_SELECTOR) !== null;
  if (want === suppressed) return;
  suppressed = want;
  send({ type: "ui.webpanes.suppress", suppress: want });
}

export function initWebPaneSuppression(): void {
  new MutationObserver(sync).observe(document.body, { childList: true });
  sync();
}
