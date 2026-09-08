import type { InspectorDataMessage } from "./bridge.js";

/** A missing/mismatched base must be refetched, never applied to other history. */
export function mergeInspector(previous: InspectorDataMessage | undefined, next: InspectorDataMessage): InspectorDataMessage | null {
  if (!next.baseRevision) return next;
  const start = next.eventStart ?? 0;
  if (!previous || previous.revision !== next.baseRevision || start < 0 || start > previous.events.length) return null;
  return { ...next, events: start === previous.events.length && !next.events.length
    ? previous.events : previous.events.slice(0, start).concat(next.events) };
}
