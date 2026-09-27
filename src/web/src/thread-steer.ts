// What a thread's "Steer this thread…" box has sent that hasn't gone in yet.
//
// A line to a thread goes in when its Claude is free: Claude Code leaves a
// line typed mid-turn sitting unsent in its composer. So a steer sent while
// the thread works waits for the turn to end — and before this was shown,
// the box simply emptied and nothing appeared, which read as "writing to a
// thread doesn't work". Pure, so the words are tested.

import type { SessionView } from "./bridge.js";

export type QueuedNote = { lines: string[]; when: string };

/** The lines waiting to go into a thread and when they will, or null. */
export function queuedNote(t: SessionView | undefined): QueuedNote | null {
  const lines = (t?.threadQueued ?? []).filter((l) => l.trim().length > 0);
  if (!t || !lines.length) return null;
  const n = lines.length === 1 ? "This goes" : "These go";
  let when: string;
  if (t.dormant) when = `${n} in once it wakes up.`;
  else if (t.agentState === "working") when = `${n} in when it finishes this turn. Stop it to send now.`;
  else if (t.agentState === "permission") when = `${n} in once you answer its permission request.`;
  else when = "Sending…";
  return { lines, when };
}

/** One queued line as the user wrote it: Perch's own prefix dropped. */
export function queuedText(line: string): string {
  return line.replace(/^From the project chat:\s*/, "");
}
