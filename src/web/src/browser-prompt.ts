// Small anchored popover to enter a URL for a new browser pane / browser tab.
// UrlPane is a fixed-URL WebView2 (no address bar), so the address is supplied
// at creation time — here. `normalizeUrl` is exported and pure for testing.

import { canOpenInPane } from "./web-url.js";

/** Normalize a user-typed address into a URL a browser pane can actually
 *  display, or null if it isn't one. Rules:
 *    - Windows drive path (C:\…) → file:///C:/…, UNC (\\host\…) → file://host/…
 *    - a real scheme passes through untouched
 *    - a bare host/path gets a scheme: http:// for localhost / loopback IPs
 *      (dev servers are http), https:// otherwise
 *    - internal whitespace, or a bare word with no dot and no :port, → null so
 *      we never open a webview pointed at garbage
 *    - FINALLY, whatever survives is checked against canOpenInPane. Every
 *      caller of this function creates a URL pane, and a pane the host refuses
 *      renders as an empty rectangle — so "about:blank", "ftp://…", or a
 *      C:\tools\setup.exe must fail HERE, where the field can shake, rather
 *      than opening a pane that silently never paints. */
export function normalizeUrl(input: string): string | null {
  const url = normalizeRaw(input);
  return url && canOpenInPane(url) ? url : null;
}

function normalizeRaw(input: string): string | null {
  const s = input.trim();
  if (!s) return null;

  // UNC path \\host\share → file://host/share
  if (s.startsWith("\\\\")) return "file:" + s.replace(/\\/g, "/");
  // Windows drive path C:\Users\… → file:///C:/Users/…
  if (/^[a-zA-Z]:[\\/]/.test(s)) return "file:///" + s.replace(/\\/g, "/");

  // Already carries a scheme — hand it back verbatim and let canOpenInPane
  // rule on it. The negative lookahead keeps "localhost:3000" (and any
  // "host:port") from reading as a scheme, since a port is always digits.
  // Without this branch catching schemeless-authority forms like
  // "mailto:someone@example.com", the bare-host path below prefixed it into
  // "https://mailto:someone@example.com" — a URL that parses fine (mailto:… as
  // userinfo) and opens a pane on the wrong host entirely.
  if (/^[a-z][a-z0-9+.-]*:(?![0-9])/i.test(s)) return s;

  // Bare host/path from here — no internal whitespace.
  if (/\s/.test(s)) return null;

  const host = s.split(/[/?#]/, 1)[0]; // authority up to first / ? #
  const isLocal = /^(localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\])(:\d+)?$/i.test(host);
  const looksHost = isLocal || /\.[^.\s]/.test(host) || /:\d+$/.test(host);
  if (!looksHost) return null;
  return (isLocal ? "http://" : "https://") + s;
}

let openPrompt: HTMLElement | null = null;

/** Show a URL-entry popover anchored under `anchor`. Calls `onSubmit(url)` with
 *  a normalized URL when the user commits a valid address; self-dismisses on
 *  Esc / click-outside. An unparseable entry shakes the field instead of
 *  submitting. */
export function showBrowserPrompt(
  anchor: HTMLElement,
  onSubmit: (url: string) => void
): void {
  dismissBrowserPrompt();

  const prompt = document.createElement("div");
  prompt.className = "browser-prompt";
  prompt.setAttribute("role", "dialog");

  const input = document.createElement("input");
  input.type = "text";
  input.className = "browser-prompt__input";
  input.placeholder = "example.com or localhost:3000";
  input.spellcheck = false;
  input.autocomplete = "off";
  prompt.appendChild(input);

  const hint = document.createElement("span");
  hint.className = "browser-prompt__hint";
  hint.textContent = "↵";
  prompt.appendChild(hint);

  const commit = () => {
    const url = normalizeUrl(input.value);
    if (!url) {
      prompt.classList.remove("browser-prompt--invalid");
      // reflow so re-adding the class restarts the shake animation
      void prompt.offsetWidth;
      prompt.classList.add("browser-prompt--invalid");
      input.focus();
      return;
    }
    dismissBrowserPrompt();
    onSubmit(url);
  };

  input.addEventListener("keydown", (ev) => {
    // Keep global keybindings (Ctrl+B, Ctrl+= …) from swallowing typing.
    ev.stopPropagation();
    if (ev.key === "Enter") {
      ev.preventDefault();
      commit();
    } else if (ev.key === "Escape") {
      ev.preventDefault();
      dismissBrowserPrompt();
    }
  });

  document.body.appendChild(prompt);
  openPrompt = prompt;

  // Anchor below the button, right-aligned to it (the button lives at the
  // header's right edge). Flip to stay on-screen.
  const rect = anchor.getBoundingClientRect();
  prompt.style.top = `${rect.bottom + 6}px`;
  const pw = prompt.getBoundingClientRect().width;
  let left = rect.right - pw;
  if (left < 8) left = 8;
  if (left + pw > window.innerWidth - 8) left = Math.max(8, window.innerWidth - pw - 8);
  prompt.style.left = `${left}px`;

  input.focus();

  setTimeout(() => {
    document.addEventListener("mousedown", promptOutside, true);
  }, 0);
}

export function dismissBrowserPrompt(): void {
  if (!openPrompt) return;
  openPrompt.remove();
  openPrompt = null;
  document.removeEventListener("mousedown", promptOutside, true);
}

function promptOutside(ev: MouseEvent) {
  if (openPrompt && !openPrompt.contains(ev.target as Node)) {
    dismissBrowserPrompt();
  }
}
