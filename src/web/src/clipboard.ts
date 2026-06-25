// Host-pushed clipboard cache.
//
// The WPF host (ClipboardWatcher + MainWindow.SyncClipboardToWeb) reads the OS
// clipboard whenever it changes while Perch is foreground, on window
// activation, and once when the page becomes ready, and ferries the text here
// via a "clipboard.text" message. Right-click paste then reads this cache
// synchronously instead of awaiting navigator.clipboard.readText().
//
// Why: in WebView2 readText() can stall hundreds of ms (window just regained
// foreground, another process holds the clipboard, large/HTML content). During
// that stall a right-click looks dead, so the user right-clicks again — and the
// second queued read pastes a second time. Reading a cache can't stall, so the
// double never starts. See pane.ts handleRightClick.

import { onMessage } from "./bridge.js";

let cached = "";

onMessage((msg) => {
  if (msg.type === "clipboard.text") cached = msg.text;
});

/** Latest clipboard text the host has pushed. "" before the first push, when
 *  the clipboard holds no text, or when it exceeds the host's size cap (the
 *  host pushes "" in that case so paste falls back to readText). */
export function cachedClipboardText(): string {
  return cached;
}

/** Mirror a local copy into the cache immediately, without waiting for the
 *  host's clipboard-change round-trip. Called from copyText() so pasting text
 *  you just selected-copied inside Perch never reads a stale cache. */
export function setCachedClipboardText(text: string): void {
  cached = text;
}
