// Agent glyphs — which agent runs in a pane, drawn as pixel art.
//
// Two marks, both taken from the thing itself rather than redrawn:
//
//   Claude Code  the creature from Claude Code's own welcome banner. The banner
//                draws it in quadrant block characters, so each terminal cell
//                is a 2×2 pixel tile — decoded here into its 18×5 pixel grid.
//                Terminal cells are twice as tall as wide, so each of those
//                pixels is 1 wide × 2 tall; the SVG keeps that aspect
//                (viewBox 18×5 drawn into 18×10) and the creature keeps its
//                shape. Claude's terracotta; the eyes are holes (the banner's
//                eyes are the terminal background showing through).
//   Codex        OpenAI's Codex mark — the lobed blob with a ">_" cut out of
//                it — rasterised from the vector at 16×16 and then tidied by
//                hand so the chevron reads as one at this size. Monochrome,
//                like the mark: currentColor, which the chrome sets to the
//                secondary text colour.
//
// Rendered as one SVG path per glyph with shape-rendering="crispEdges", so the
// pixels stay square at any zoom instead of blurring. Scaled by whole CSS
// pixels only (a 1.5× unit turns some pixels into two device pixels and
// others into one, and the art wobbles). Nothing here touches the DOM at
// import time; the sprites and the path builder are pure and unit-tested.

export interface Sprite {
  /** Rows of '#' (pixel) and '.' (empty), all the same length. */
  rows: readonly string[];
  /** How many CSS pixels tall one sprite pixel is (its width is always 1). */
  pixelHeight: number;
  /** Tooltip / accessible name. */
  label: string;
}

/** The welcome-banner creature: 18 wide, 5 tall, each pixel 1×2. Eyes at
 *  columns 5 and 12, four legs on the bottom row. */
export const CLAUDE_SPRITE: Sprite = {
  rows: [
    "...############...",
    "...##.######.##...",
    ".################.",
    "...############...",
    "....#.#....#.#....",
  ],
  pixelHeight: 2,
  label: "Claude Code",
};

/** The Codex mark at 16×16: the blob, with the ">" (rows 5–10, its tip on
 *  rows 7–8) and the "_" (row 10) cut out. */
export const CODEX_SPRITE: Sprite = {
  rows: [
    ".....####.......",
    "....#########...",
    "...###########..",
    "..#############.",
    ".##############.",
    "####.##########.",
    "#####.#########.",
    "######.#########",
    "######.#########",
    ".####.##########",
    ".###.###....####",
    ".##############.",
    ".#############..",
    "..###########...",
    "...#########....",
    ".......####.....",
  ],
  pixelHeight: 1,
  label: "Codex",
};

/** Which sprite an agentType wears. Undefined for a plain shell or an agent
 *  we have no mark for. */
export function spriteFor(agentType: string | undefined): Sprite | undefined {
  if (agentType === "claude") return CLAUDE_SPRITE;
  if (agentType === "codex") return CODEX_SPRITE;
  return undefined;
}

/** The sprite's pixels as one SVG path in sprite units: each horizontal run
 *  of '#' becomes one 1-tall rectangle, so a 16×16 glyph is a few dozen
 *  subpaths rather than a few hundred. */
export function spritePath(rows: readonly string[]): string {
  let d = "";
  rows.forEach((row, y) => {
    let x = 0;
    while (x < row.length) {
      if (row[x] !== "#") { x++; continue; }
      let w = 0;
      while (row[x + w] === "#") w++;
      d += `M${x} ${y}h${w}v1h-${w}z`;
      x += w;
    }
  });
  return d;
}

/** Sprite width in pixels (the rows' length). */
export function spriteWidth(s: Sprite): number { return s.rows[0]?.length ?? 0; }
/** Sprite height in CSS pixels at scale 1 (rows × pixel height). */
export function spriteHeight(s: Sprite): number { return s.rows.length * s.pixelHeight; }

const SVG_NS = "http://www.w3.org/2000/svg";

/** Build the glyph for an agent, or null when it has none. `scale` is the
 *  whole number of CSS pixels per sprite pixel. The element is a plain SVG
 *  with class `agent-glyph` and `data-agent`; callers position it. */
export function agentGlyph(agentType: string | undefined, scale = 1): SVGSVGElement | null {
  const sprite = spriteFor(agentType);
  if (!sprite) return null;
  const w = spriteWidth(sprite);
  const h = sprite.rows.length;
  const svg = document.createElementNS(SVG_NS, "svg");
  svg.setAttribute("class", `agent-glyph agent-glyph--${agentType}`);
  svg.setAttribute("viewBox", `0 0 ${w} ${h}`);
  svg.setAttribute("width", String(w * scale));
  svg.setAttribute("height", String(h * sprite.pixelHeight * scale));
  // A non-square pixel is a non-uniform scale: let the viewBox stretch.
  if (sprite.pixelHeight !== 1) svg.setAttribute("preserveAspectRatio", "none");
  svg.setAttribute("shape-rendering", "crispEdges");
  svg.setAttribute("role", "img");
  svg.setAttribute("aria-label", sprite.label);
  svg.dataset.agent = agentType!;
  const title = document.createElementNS(SVG_NS, "title");
  title.textContent = sprite.label;
  svg.appendChild(title);
  const path = document.createElementNS(SVG_NS, "path");
  path.setAttribute("d", spritePath(sprite.rows));
  // Colour lives in CSS (.agent-glyph--claude / --codex) so both themes and
  // the header's hover states can tune it; the path just inherits.
  path.setAttribute("fill", "currentColor");
  svg.appendChild(path);
  return svg;
}
