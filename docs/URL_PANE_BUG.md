# URL pane WebView2 sizing — known bug

## Symptom

The first time a URL pane is created at some size, its WebView2 renders correctly
at that size. After the main window resizes — drag-edge, maximize, restore — the
embedded WebView2 **does not track the new size**. The pane's CSS placeholder
(`.pane__urlcontent`) reflows correctly to the new inset rect, the page emits
fresh `urlpane.layout` IPC, the host calls `Controller.Bounds = newRect`, but
the WebView2 stays at the *create-time* size and position. Visually you see the
slot's terminal-bg color where the WebView2 should have grown into.

Anchoring is curious: the WebView2 stays anchored bottom-left of the pane. As
the window grows, magenta padding (debug build) appears on **top + right** but
not bottom/left.

## What we tried (chronological)

1. **WPF Window (`UrlPaneWindow`) + `SetParent` to reparent its HWND as child
   of the main window.** Worked for moves (Win32 child windows track parent
   moves natively), but not for resizes. The WPF Window kept its own size model
   even when we `MoveWindow`/`SetWindowPos`'d the HWND — the embedded WebView2
   inside `Content` re-laid-out to WPF's modeled size, not the Win32 HWND size.
2. **`ApplySize` that sets `Width`/`Height` (DIPs) BEFORE `SetWindowPos`
   (pixels).** Made WPF and Win32 agree on the size in theory. In practice, no
   visible change — the WebView2's inner HWND still rendered at the original
   create-time bounds.
3. **Replaced `UrlPaneWindow` entirely with `CoreWebView2Controller` via
   `CreateCoreWebView2ControllerAsync(parentHwnd)` + `Controller.Bounds`.** No
   WPF Window in the path. Microsoft's official recipe for nested WebView2s.
   First-render at create bounds works. Subsequent `Controller.Bounds` updates
   on resize **silently no-op** — verified by debug colors showing the slot's
   background where the WebView2 should have grown.
4. **`EnumChildWindows` + `SetWindowPos(HWND_TOP, ..., SWP_NOMOVE|SWP_NOSIZE)`
   on the new WebView2 HWND** to defeat z-order conflict with the main
   WebView2's HwndHost. Brought visible rendering back in some configurations
   but the resize-stale-bounds bug persists.
5. **`forceRefit()` resets `lastRect = {-1,-1,-1,-1}` + double-rAF before
   reading `getBoundingClientRect`.** Ensures the page always re-sends and that
   CSS has laid out before the read. Confirmed the page IS sending updated
   rects on every resize. Host-side `OnLayout` IS being called. The
   `Controller.Bounds` setter IS being invoked. The WebView2 still doesn't
   resize.

## Hypotheses for the next iteration

- **`CoreWebView2Controller.Bounds` may not propagate to the underlying HWND
  child** in all configurations, particularly when the controller's
  `ParentWindow` is shared with another WebView2 (the main one). Try setting
  `IsVisible = false` → `Bounds = newRect` → `IsVisible = true` to force a
  layout pass. The `RasterizationScale` property might also need a kick.
- **The new WebView2's inner HWND** (the actual rendering surface inside the
  controller) might need a manual `SetWindowPos` to resize. The controller
  exposes neither the HWND nor a Resize() method directly, but we can find it
  via `EnumChildWindows` after init.
- **Try a fresh `CoreWebView2Environment` per URL pane** instead of sharing
  with the main WebView2. Multiple controllers sharing one environment may
  share an internal render thread that doesn't handle per-controller resizes
  cleanly. (More expensive; 50ms+ per first navigate.)
- **Skip the rect-IPC round-trip entirely on resize.** The Win32 child HWND
  approach (option 1) was correct for moves; the resize problem is specific to
  the embedded WebView2 not tracking its container. If we go back to a top-
  level Window approach BUT explicitly call `MoveWindow` on the WebView2's
  inner HWND (found via EnumChildWindows), we sidestep WPF's layout entirely.
- **Composition mode** (`Microsoft.Web.WebView2.Wpf.WebView2CompositionControl`
  internal class, or the lower-level `CoreWebView2CompositionController` +
  DComp visual hosting) is the architecturally correct answer but requires
  significant DComp interop. Worth a half-day investment if other options fail.

## Where the code currently lives

- `src/Perch/UrlPaneHost.cs` — wraps `CoreWebView2Controller` with parent
  HWND + Bounds. Logs `UrlPaneHost.Init.begin` and `UrlPaneHost.Init.ztop`.
- `src/Perch/UrlPaneController.cs` — dispatches page IPC, calls
  `host.SetBounds` on each `urlpane.layout`. Auto-title pipeline still works.
- `src/web/src/url-pane.ts` — page-side placeholder + position reporter.
  `forceRefit()` invalidates the rect cache.
- Debug visualization (magenta slot bg, lime content outline, red WebView2
  default bg) was removed in this commit. Re-enable in style.css +
  `UrlPaneHost.InitAsync` if debugging.

## Reproduction

1. Launch the app.
2. `echo http://google.com` in any terminal pane.
3. Click the URL → "Open in pane right" → URL pane renders correctly.
4. **Maximize the perch window** (or drag any edge to a much larger size).
5. The Google WebView2 stays at its old smaller size; you'll see the slot
   background in the area where the pane grew but the WebView2 didn't.
