// Keeps a scrolling conversation on its latest line, and offers "Go to latest"
// once you've scrolled away from it.
//
// The log follows new content only while you're at the end ("pinned"). Only
// YOU unpin it: a scroll that isn't yours (a re-attach resetting scrollTop to
// 0, a smooth scroll in flight, content growing under a rendered Markdown
// block) never counts as leaving the end. A ResizeObserver on the scroller and
// its content re-lands the end whenever either changes size while pinned —
// that covers the cases a one-shot rAF missed: switching back to the tab
// (display:none → shown), code blocks and images laying out late, and the
// composer or a card below growing.

import { el, icon, reducedMotion } from "./thread-ui.js";

/** Within this many px of the bottom counts as "at the end". */
export const END_SLACK = 48;
/** A scroll this soon after your wheel / touch / key / pointer is yours. */
const USER_WINDOW_MS = 1200;

export function isAtEnd(scrollHeight: number, scrollTop: number, clientHeight: number, slack = END_SLACK): boolean {
  return scrollHeight - scrollTop - clientHeight <= slack;
}

/** Whether the log stays pinned after a scroll: reaching the end always pins;
 *  only a scroll you made unpins. */
export function nextPinned(pinned: boolean, atEnd: boolean, byUser: boolean): boolean {
  if (atEnd) return true;
  return byUser ? false : pinned;
}

export class StickToEnd {
  readonly button: HTMLButtonElement;
  private readonly holder: HTMLElement;
  private pinned = true;
  private userAt = 0;
  private dragging = false;
  private lastHeight = 0;
  private smoothing = false;
  private smoothTimer = 0;
  private readonly ro: ResizeObserver;
  private readonly onUp = () => { this.dragging = false; };

  constructor(private readonly scroll: HTMLElement) {
    // A zero-height sticky row at the end of the scroller: the button floats
    // at the bottom of the view without taking a line of the log.
    this.holder = el("div", "stick-end");
    this.button = el("button", "stick-end__btn") as HTMLButtonElement;
    this.button.type = "button";
    this.button.append(icon("down"), el("span", undefined, "Go to latest"));
    this.button.setAttribute("aria-label", "Go to latest");
    this.button.addEventListener("click", (e) => { e.stopPropagation(); this.toEnd(true); });
    this.holder.appendChild(this.button);
    this.holder.hidden = true;
    scroll.appendChild(this.holder);

    const mark = () => { this.userAt = performance.now(); };
    for (const t of ["wheel", "touchmove", "keydown"]) scroll.addEventListener(t, mark, { passive: true });
    scroll.addEventListener("pointerdown", () => { mark(); this.dragging = true; });
    window.addEventListener("pointerup", this.onUp);
    scroll.addEventListener("scroll", () => {
      if (!scroll.clientHeight) return;                 // hidden: nothing to judge
      const byUser = this.dragging || performance.now() - this.userAt < USER_WINDOW_MS;
      this.pinned = nextPinned(this.pinned, this.atEnd(), byUser);
      // Moved off the end by something that isn't you (a re-attach resets
      // scrollTop to 0): put it back.
      if (this.pinned && !this.atEnd() && !this.smoothing) scroll.scrollTop = scroll.scrollHeight;
      this.sync();
    }, { passive: true });

    this.ro = new ResizeObserver(() => {
      if (!scroll.clientHeight) return;
      const grew = scroll.scrollHeight > this.lastHeight;
      this.lastHeight = scroll.scrollHeight;
      if (this.pinned) scroll.scrollTop = scroll.scrollHeight;
      else if (grew) this.button.classList.add("stick-end__btn--fresh");   // something new below
      this.sync();
    });
    this.ro.observe(scroll);
    for (const c of scroll.children) if (c !== this.holder) this.ro.observe(c);
  }

  /** Keep the button the scroller's last child after content is appended. */
  private keepLast() {
    if (this.scroll.lastElementChild !== this.holder) this.scroll.appendChild(this.holder);
  }

  isPinned(): boolean { return this.pinned; }

  /** Land on the end and follow from here. */
  toEnd(smooth: boolean) {
    this.pinned = true;
    this.keepLast();
    this.button.classList.remove("stick-end__btn--fresh");
    const s = this.scroll;
    if (smooth && !reducedMotion()) {
      // Let a smooth scroll run its course; the scroll handler must not snap it.
      this.smoothing = true;
      window.clearTimeout(this.smoothTimer);
      this.smoothTimer = window.setTimeout(() => { this.smoothing = false; }, 600);
      s.scrollTo({ top: s.scrollHeight, behavior: "smooth" });
    } else s.scrollTop = s.scrollHeight;
    this.sync();
  }

  /** Follow new content only if already at the end (or `force`). */
  follow(force = false) {
    if (force) this.toEnd(true);
    else if (this.pinned) this.toEnd(false);
  }

  dispose() {
    this.ro.disconnect();
    window.removeEventListener("pointerup", this.onUp);
  }

  private atEnd(): boolean {
    const s = this.scroll;
    return isAtEnd(s.scrollHeight, s.scrollTop, s.clientHeight);
  }

  private sync() {
    this.keepLast();
    const show = !this.pinned && !this.atEnd();
    this.holder.hidden = !show;
    if (!show) this.button.classList.remove("stick-end__btn--fresh");
  }
}
