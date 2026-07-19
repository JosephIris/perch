// Per-pane header action buttons: split this pane into a new terminal to the
// right / below, or open an embedded browser pane beside it. Mirrors the mac
// cmux pane controls, sitting left of the ✕ close button.
//
// The split/browser messages are the SAME ones the URL link-menu sends
// (pane.split, optionally with a url), so the host needs no new handling —
// these are just always-available entry points in the pane header. "browser"
// asks for a URL first (UrlPane has no address bar) via showBrowserPrompt.

import { send, type OutMessage } from "./bridge.js";
import { showBrowserPrompt } from "./browser-prompt.js";

export type PaneAction = "split-right" | "split-down" | "browser";

/** Pure action→message mapping so the wiring is unit-testable without a DOM.
 *  "browser" needs a url (from the prompt); returns null when it's absent so
 *  the caller no-ops instead of splitting into a blank webview. */
export function paneActionMessage(
  action: PaneAction,
  paneId: string,
  url?: string
): OutMessage | null {
  switch (action) {
    case "split-right": return { type: "pane.split", paneId, dir: "right" };
    case "split-down":  return { type: "pane.split", paneId, dir: "down" };
    case "browser":     return url ? { type: "pane.split", paneId, dir: "right", url } : null;
  }
}

interface ActionDef {
  action: PaneAction;
  label: string; // aria-label + tooltip
  icon: string;  // inline SVG path data, 24×24 viewBox
}

const ACTIONS: ActionDef[] = [
  // panel-right: outer rect + a divider toward the right edge.
  { action: "split-right", label: "Split right (new terminal)", icon: "M3 5h18v14H3z M14 5v14" },
  // panel-bottom: outer rect + a divider along the bottom.
  { action: "split-down", label: "Split down (new terminal)", icon: "M3 5h18v14H3z M3 14h18" },
  // globe: circle + equator + two meridians.
  { action: "browser", label: "Open browser pane", icon: "M12 3a9 9 0 100 18 9 9 0 000-18z M3 12h18 M12 3a14 14 0 010 18 M12 3a14 14 0 000 18" },
];

function actionIcon(d: string): SVGSVGElement {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("class", "pane__action-icon");
  svg.setAttribute("width", "14");
  svg.setAttribute("height", "14");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "1.6");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
  path.setAttribute("d", d);
  svg.appendChild(path);
  return svg;
}

/** Build the pane-header action button group. Inserted before the ✕ close
 *  button; hidden until the pane is hovered/active (styling in style.css). */
export function buildPaneActions(paneId: string): HTMLElement {
  const group = document.createElement("div");
  group.className = "pane__actions";

  for (const def of ACTIONS) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "pane__action";
    // The header owns dragging (HTML5 DnD); its buttons opt out like the
    // color / model / close controls do.
    btn.draggable = false;
    btn.title = def.label;
    btn.setAttribute("aria-label", def.label);
    btn.dataset.action = def.action;
    btn.appendChild(actionIcon(def.icon));

    btn.addEventListener("click", (ev) => {
      ev.stopPropagation();
      if (def.action === "browser") {
        showBrowserPrompt(btn, (url) => {
          const msg = paneActionMessage("browser", paneId, url);
          if (msg) send(msg);
        });
      } else {
        const msg = paneActionMessage(def.action, paneId);
        if (msg) send(msg);
      }
    });

    group.appendChild(btn);
  }

  return group;
}
