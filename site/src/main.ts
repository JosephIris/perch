// Landing page behavior: the hero scene is the SAME rig the app ships
// (imported from setup-overlay.ts, not copied), performing the walk-in and
// the shuffled idle routine on a loop. Click replays with a fresh shuffle.

import {
  pose, buildScene, shuffledOrder, REST_T,
  type BeatOrder,
} from "../../src/web/src/setup-overlay.js";

const host = document.getElementById("hero-scene");
if (host) {
  const scene = buildScene();
  // The rig's viewBox is padded for the in-app overlay; on the page, crop to
  // the band the performance actually uses so the hero stays tight.
  scene.svg.setAttribute("viewBox", "0 35 320 100");
  host.appendChild(scene.svg);

  const reduce = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
  if (reduce) {
    // Still a bird, still perched; just no performance.
    scene.apply(pose(REST_T));
    host.style.cursor = "default";
  } else {
    let order: BeatOrder = shuffledOrder();
    let t0 = performance.now();
    const tick = (now: number) => {
      scene.apply(pose(now - t0, order));
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
    host.addEventListener("click", () => {
      order = shuffledOrder();
      t0 = performance.now();
    });
  }
}
