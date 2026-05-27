// Shared animation durations (ms). Keep in sync with the matching CSS
// transition/animation durations in style.css; the keyframes that define
// the visual run via `var(--dur-normal)` etc. and these JS constants
// schedule the post-animation work (dispose, commit-render) AFTER the
// CSS has played out.
//
// Why a separate file? Same value was previously hard-coded in 3+
// places — every time we want to tune the feel we'd have to chase the
// magic numbers across workspace.ts, url-pane.ts, etc.

export const PANE_LEAVE_MS  = 200;   // .pane--leaving fade duration
export const PANE_ENTER_MS  = 200;   // .pane--entering fade-in duration
export const SESSION_SWAP_MS = 180;  // .workspace--switching crossfade
