// Framed glyph buttons for the room's panel heads: a 24 px hairline frame
// around one single-stroke glyph, named on hover by the app tooltip instead
// of a word beside it. The task board's "+" and the artefact panel's list /
// wide / tab / window actions are these. A button's glyph and name can be
// swapped in place (the "+" becomes a "✕" while the composer is open), so the
// tooltip reads the name lazily from the element.

import { attachTooltip } from "./tooltip.js";

export type GlyphName = "plus" | "close" | "list" | "wide" | "narrow" | "tab" | "window" | "expand";

/* 24-unit stroke paths, Fluent-style: round caps, 1.6 stroke, no fills. */
const PATHS: Record<GlyphName, string[]> = {
  plus:   ["M12 5v14", "M5 12h14"],
  close:  ["M6 6l12 12", "M18 6 6 18"],
  // A list: three lines with a dot before each.
  list:   ["M9 6h11", "M9 12h11", "M9 18h11", "M4 6h.01", "M4 12h.01", "M4 18h.01"],
  // Arrows apart / arrows together.
  wide:   ["M10 12H3", "M6 9l-3 3 3 3", "M14 12h7", "M18 9l3 3-3 3"],
  narrow: ["M3 12h7", "M7 9l3 3-3 3", "M21 12h-7", "M17 9l-3 3 3 3"],
  // A browser tab strip: the frame, its bar, one tab's edge.
  tab:    ["M3 6.5A1.5 1.5 0 0 1 4.5 5h15A1.5 1.5 0 0 1 21 6.5v11a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17.5z", "M3 10h18", "M9 5v5"],
  // Out of the frame: the "open elsewhere" arrow.
  window: ["M14 4h6v6", "M20 4l-9 9", "M11 5H6.5A2.5 2.5 0 0 0 4 7.5v10A2.5 2.5 0 0 0 6.5 20h10a2.5 2.5 0 0 0 2.5-2.5V13"],
  // Four corners: see it large.
  expand: ["M4 9V4h5", "M20 9V4h-5", "M4 15v5h5", "M20 15v5h-5"],
};

const SVG_NS = "http://www.w3.org/2000/svg";

function glyphSvg(name: GlyphName): SVGSVGElement {
  const svg = document.createElementNS(SVG_NS, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "1.6");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  svg.setAttribute("aria-hidden", "true");
  for (const d of PATHS[name]) {
    const p = document.createElementNS(SVG_NS, "path");
    p.setAttribute("d", d);
    svg.appendChild(p);
  }
  return svg;
}

/** Swap a glyph button's picture and name in place. */
export function setGlyph(btn: HTMLButtonElement, name: GlyphName, label: string): void {
  btn.dataset.glyph = name;
  btn.dataset.label = label;
  btn.setAttribute("aria-label", label);
  btn.replaceChildren(glyphSvg(name));
}

/** A framed glyph button (class `team-glyph`), its name shown on hover. */
export function glyphButton(name: GlyphName, label: string, onClick: () => void, extraClass = ""): HTMLButtonElement {
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "team-glyph" + (extraClass ? " " + extraClass : "");
  setGlyph(btn, name, label);
  btn.addEventListener("click", onClick);
  attachTooltip(btn, () => btn.dataset.label ?? "");
  return btn;
}
