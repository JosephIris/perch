// The serif greeting the full-window views open with ("Good evening, Joseph."),
// the same voice as a project chat's Overview. The name rides on every state
// push (StateProjection.BuildSnapshot → userName); "" until the host has read it.

let name = "";

export function setUserName(n: string | undefined): void { name = (n ?? "").trim(); }

/** "Good morning/afternoon/evening, Joseph." by the local clock. */
export function greetingText(now = new Date()): string {
  const h = now.getHours();
  const part = h >= 5 && h < 12 ? "morning" : h >= 12 && h < 18 ? "afternoon" : "evening";
  return name ? `Good ${part}, ${name}.` : `Good ${part}.`;
}

/** Play the views' arrival once — the greeting and its content rise in. Only
 *  on open: the body re-renders on every state push and must not replay it. */
export function playEntrance(root: HTMLElement): void {
  root.classList.remove("overlay--enter");
  void root.offsetWidth;
  root.classList.add("overlay--enter");
  window.setTimeout(() => root.classList.remove("overlay--enter"), 600);
}
