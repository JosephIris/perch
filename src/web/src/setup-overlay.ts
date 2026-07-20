// Per-pane "Setting up…" overlay — a frosted cover shown while a Claude Code
// pane boots, then hidden. It replaces the old trick of typing `/color` into
// cc's raw PTY (fragile: it raced cc's input reader and could concatenate onto
// whatever the user had already typed). Instead we cover the pane during the
// boot window so nothing lands in cc, and the frost tints to the pane's color.
//
// The mascot ("Monocle Guy") lays a twig on a woven-cup nest; only the bird
// moves (the nest is fixed). Markup mirrors design-loop/setup-overlay-*.html —
// keep the two in sync when iterating on the art.

const LOADER_SVG = `
<svg class="setup-loader" viewBox="0 0 150 130" aria-hidden="true">
  <g class="setup-scene">
    <!-- NEST: woven cup, drawn before the bird so the placed twig rests on the rim -->
    <g fill="none" stroke="currentColor" stroke-linecap="round">
      <path d="M58 100 Q98 90 138 100" stroke-width="2.2" opacity=".4"/>
      <path d="M66 97 Q98 91 130 97" stroke-width="1.5" opacity=".28"/>
      <path d="M84 92 L80 82"  stroke-width="1.9" opacity=".45"/>
      <path d="M100 90 L106 80" stroke-width="1.9" opacity=".45"/>
      <path d="M116 92 L124 84" stroke-width="1.8" opacity=".38"/>
      <path d="M70 95 L63 87"  stroke-width="1.7" opacity=".33"/>
      <path d="M58 100 Q98 111 138 100" stroke-width="2.4" opacity=".72"/>
      <path d="M61 104 Q98 114 135 104" stroke-width="1.9" opacity=".58"/>
      <path d="M62 108 Q98 117 134 108" stroke-width="1.8" opacity=".48"/>
      <path d="M67 111 Q98 118 129 111" stroke-width="1.6" opacity=".4"/>
      <path d="M74 101 L88 106"  stroke-width="1.6" opacity=".55"/>
      <path d="M92 106 L106 100" stroke-width="1.6" opacity=".55"/>
      <path d="M110 101 L124 106" stroke-width="1.5" opacity=".5"/>
      <path d="M138 104 l7 2" stroke-width="1.7" opacity=".38"/>
      <path d="M58 104 l-7 2" stroke-width="1.7" opacity=".38"/>
    </g>
    <!-- BIRD RIG: full bird + beak + diagonally-laid twig (only this group moves) -->
    <g class="setup-bird">
      <g fill="none" stroke="currentColor" stroke-linecap="round">
        <path d="M97 57 Q111 72 121 94" stroke-width="2.7"/>
        <path d="M112 85 q4.5 -1 7.5 -0.5" stroke-width="2"/>
      </g>
      <circle cx="121" cy="94" r="1.8" fill="currentColor"/>
      <g transform="translate(3 20) scale(4.25)">
        <g transform="translate(-0.6 -3.4) rotate(15 12.3 15.6)">
          <path fill="none" stroke="currentColor" stroke-width="1.0" stroke-linecap="round" d="M11.3 14.1 V 15.7 M13.4 14.1 V 15.7"/>
          <path fill="currentColor" d="M19.9 10.0 C 19.5 7.9, 17.6 6.3, 15.4 6.5 C 13.9 6.5, 12.5 6.8, 11.4 7.4 C 9.4 7.6, 7.4 6.2, 5.7 6.7 C 5.1 6.9, 5.2 7.6, 5.9 8.1 C 7.0 8.9, 7.7 10.1, 8.5 11.1 C 9.6 12.7, 10.9 14.4, 12.6 14.4 C 14.0 14.4, 15.2 13.9, 16.1 13.0 C 17.1 12.4, 18.0 11.9, 18.6 11.2 C 19.1 10.9, 19.6 10.6, 19.9 10.0 Z"/>
          <path fill="currentColor" d="M17.4 9.4 L 21.9 10.3 L 18.2 11.35 Z"/>
          <path fill="none" stroke="currentColor" stroke-width="0.5" stroke-linecap="round" d="M20.2 10.8 L 21.6 10.5"/>
          <path fill="none" stroke="#15233b" stroke-width="0.75" stroke-linecap="round" d="M15.9 7.3 C 16.55 7.25, 17.3 7.5, 17.9 8.0"/>
          <circle cx="17" cy="9" r="0.95" fill="#15233b"/>
          <circle cx="17" cy="9" r="1.95" fill="none" stroke="var(--color-accent)" stroke-width="0.6"/>
          <path fill="none" stroke="var(--color-accent)" stroke-width="0.5" stroke-linecap="round" d="M17.6 10.8 C 17.8 11.7, 17.5 12.5, 16.9 13.1"/>
        </g>
      </g>
    </g>
    <!-- nestle twigs laced over the placed tip -->
    <g fill="none" stroke="currentColor" stroke-linecap="round">
      <path d="M113 98 L129 92" stroke-width="1.6" opacity=".55"/>
      <path d="M116 100 L131 95" stroke-width="1.4" opacity=".48"/>
    </g>
  </g>
</svg>`;

export interface SetupOverlay {
  /** The root element to append to the pane. */
  readonly el: HTMLElement;
  /** Tint the frost to a pane color-tag index (0..5). */
  setColor(colorIndex: number): void;
}

/** Build a hidden setup overlay for a pane. Caller appends `el` to the pane
 *  root, toggles visibility via the `hidden` attribute, and manages focus. */
export function createSetupOverlay(): SetupOverlay {
  const el = document.createElement("div");
  el.className = "setup-overlay";
  el.tabIndex = -1;
  el.hidden = true;
  // The tint glow lives on __art, not on the overlay root: the root's center is
  // the PANE's center, but the art sits above it (the stack is svg + caption),
  // so a root-anchored gradient always read high and off to one side of the
  // bird. Anchored here it tracks the art no matter how the pane is sized.
  el.innerHTML =
    `<div class="setup-overlay__stack">` +
    `<div class="setup-overlay__art">${LOADER_SVG}</div>` +
    `<div class="setup-overlay__caption">Setting up` +
    `<span class="setup-overlay__dots"></span></div></div>`;

  // Swallow keystrokes while up so nothing leaks past the cover (the terminal
  // is blurred by the pane, but this guards stray page-level defaults too).
  el.addEventListener("keydown", (e) => {
    e.preventDefault();
    e.stopPropagation();
  });

  return {
    el,
    setColor(colorIndex: number) {
      const i = ((colorIndex % 6) + 6) % 6;
      el.style.setProperty("--setup-hint", `var(--color-pane-tag-${i})`);
    },
  };
}
