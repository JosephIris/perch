// Detection for Claude Code's permission dialog in a terminal buffer tail.
//
// The host's agent state is edge-triggered off cc hooks, and two of the ways
// OUT of a permission prompt fire no hook at all (Esc aborts the turn without
// a Stop; "no + feedback" resumes without running the asked-about tool), while
// an approved long-running tool only fires PostToolUse when it FINISHES. Any
// of those leaves the pane wearing a red "permission" it no longer deserves.
//
// The page owns the one thing that can't go stale: the terminal buffer. While
// the host says "permission", pane.ts peeks at the bottom of the buffer and
// asks this module whether the dialog is plausibly still on screen; when it
// isn't, the host walks the state back. Kept free of xterm imports so the
// decision logic is unit-testable in node.
//
// Recall is what matters in the regex: a marker falsely PRESENT (Claude's own
// reply text saying "do you want to…") merely delays the heal; a dialog we
// fail to see would demote a genuinely blocked agent. So match generously —
// the numbered ❯ selector every cc dialog has, plus its option phrasings.
// "esc to interrupt" is deliberately NOT a marker: that's the WORKING
// spinner's hint, i.e. exactly the state we want to heal into.
//
// Codex's approval card is matched too (a codex pane wears the same
// "permission" state): its question is "Would you like to run/make/grant/
// send…?", its options "Yes, proceed" and "No, and tell Codex what to do
// differently". Without these the probe saw no Claude markers on a codex
// card and healed a genuinely blocked codex pane back to working.
export const PERM_DIALOG_RE =
  /❯\s*\d+\.|do you want to|would you like to|don['’]t ask again|tell (claude|codex) what to do|yes, proceed/i;

/** Whether the permission dialog is plausibly still on screen given the tail
 *  of the pane's buffer. A nearly-empty tail can't prove absence (fresh page
 *  reload, output not yet replayed), so it counts as visible — the bias is
 *  always toward keeping a blocked agent loud. */
export function permissionDialogVisible(tail: string): boolean {
  if (tail.replace(/\s/g, "").length < 40) return true;
  return PERM_DIALOG_RE.test(tail);
}

// The INVERSE detection: a blocked dialog sitting on a pane the host thinks
// is at rest (question dialogs, plan approval, a permission prompt whose
// notification was lost — none of which the calm states can see; the watchdog
// demotes their silence to "done" and a blocked agent reads as idle).
//
// Here the bias flips: this regex PROMOTES a calm pane to "Needs you", so
// precision matters and the phrases require the dialog's box-drawing border —
// Claude's own prose loves "do you want me to…?" and sits at exactly the same
// buffer bottom, but prose isn't boxed. The bare ❯ selector stays because
// every cc dialog (and menu) draws it; the host additionally gates promotion
// to inactive panes whose "done" was itself only inferred, which is what
// keeps a user-driven /model picker from crying wolf.
export const BLOCKED_DIALOG_RE =
  /│.*?(do you want|would you like)|❯\s*\d+\.|don['’]t ask again|tell claude what to do/i;

/** Whether a blocked (input-wanting) dialog is on screen. Empty tail counts
 *  as NOT visible — promotion needs positive evidence, the mirror image of
 *  permissionDialogVisible's bias. */
export function blockedDialogVisible(tail: string): boolean {
  if (tail.replace(/\s/g, "").length < 40) return false;
  return BLOCKED_DIALOG_RE.test(tail);
}
