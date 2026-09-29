// What the sidebar's Inbox row shows. Its own module so it tests without a
// DOM (inbox.ts pulls in the mail reader).

import type { InboxStateMessage } from "./bridge.js";

/** The counts that want you and whether the sync is broken (so the list is
 *  going stale), plus the row's tooltip. */
export function inboxSummary(msg: Pick<InboxStateMessage, "status" | "needsLogin" | "loggingIn" | "counts" | "exportProblem">):
  { fresh: number; pending: number; problem: "login" | "error" | "export" | null; title: string } {
  const fresh = msg.counts.new ?? 0;
  const pending = msg.counts.pending ?? 0;
  const problem = msg.needsLogin && !msg.loggingIn ? "login" : msg.status === "error" ? "error"
    : msg.exportProblem ? "export" : null;
  const parts = ["Open inbox"];
  if (problem === "login") parts.push("sign in to gcloud to keep it syncing");
  else if (problem === "error") parts.push("the last sync failed");
  else if (problem === "export") parts.push("the Gmail export is stuck");
  if (fresh) parts.push(`${fresh} new`);
  if (pending) parts.push(`${pending} pending`);
  return { fresh, pending, problem, title: parts.join(" · ") };
}
