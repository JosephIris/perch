// Data layer for the "ready to push" recap. One place owns fetching the
// unpushed-commit list per pane so the three surfaces (footer chip popover,
// sidebar hover tooltip, sidebar/dashboard lightbox) share a single request
// path and cache. The host replies to commits.request with commits.data; we
// resolve the matching in-flight promise and memoize the result.
//
// Caching contract: a fetched list is cached per paneId until its ahead-count
// changes (a push, a new commit). main.ts calls invalidateCommits() for every
// leaf on each state push, so the next hover/open refetches when — and only
// when — the count moved. Cheap git plumbing + a deliberate, infrequent
// trigger (450ms hover dwell / a click) keep the refetch rate low.

import { send, onMessage, type CommitsDataMessage } from "./bridge.js";

const cache = new Map<string, CommitsDataMessage>();

type Pending = {
  promise: Promise<CommitsDataMessage>;
  resolve: (d: CommitsDataMessage) => void;
  timer: number;
};
const inflight = new Map<string, Pending>();

// Safety net: the host always replies, but if it throws before posting we
// don't want a popover stuck on "Loading…" forever. Resolve empty after this.
const FETCH_TIMEOUT_MS = 5000;

onMessage((msg) => {
  if (msg.type !== "commits.data") return;
  cache.set(msg.paneId, msg);
  const p = inflight.get(msg.paneId);
  if (p) {
    clearTimeout(p.timer);
    inflight.delete(msg.paneId);
    p.resolve(msg);
  }
});

/** Fetch (or return cached) unpushed commits for a pane. Concurrent calls for
 *  the same pane share one request. Never rejects — a timeout resolves to an
 *  empty list so callers can render a "couldn't load" state. */
export function requestCommits(paneId: string): Promise<CommitsDataMessage> {
  const cached = cache.get(paneId);
  if (cached) return Promise.resolve(cached);

  const existing = inflight.get(paneId);
  if (existing) return existing.promise;

  let resolve!: (d: CommitsDataMessage) => void;
  const promise = new Promise<CommitsDataMessage>((r) => (resolve = r));
  const timer = window.setTimeout(() => {
    inflight.delete(paneId);
    resolve({ type: "commits.data", paneId, ahead: 0, commits: [] });
  }, FETCH_TIMEOUT_MS);
  inflight.set(paneId, { promise, resolve, timer });
  send({ type: "commits.request", paneId });
  return promise;
}

/** Drop the cached list for a pane when its known ahead-count changes, so the
 *  next request refetches. No-op when nothing is cached or the count matches. */
export function invalidateCommits(paneId: string, ahead: number): void {
  const cached = cache.get(paneId);
  if (cached && cached.ahead !== ahead) cache.delete(paneId);
}
