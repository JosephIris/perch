using System;
using System.Threading.Tasks;

namespace Perch;

/// Normalized "a webview process died" signal. Windows maps WebView2's
/// ProcessFailed kinds onto this; WKWebView only ever reports RenderExited
/// (webViewWebContentProcessDidTerminate).
internal enum WebViewFailureKind
{
    BrowserExited,
    RenderExited,
    RenderUnresponsive,
    /// Auto-recovered by the engine (GPU/utility process) — log only.
    Recoverable,
}

internal sealed record WebViewFailure(WebViewFailureKind Kind, string Detail);

/// The main webview, as AppController consumes it. The entire host↔page
/// contract is: serve the bundle, pass JSON strings both ways, navigate,
/// and surface process death.
internal interface IWebViewHost
{
    /// Create the browser/webview. False when the web bundle directory is
    /// missing — the controller then shows BootstrapHtml instead.
    Task<bool> InitAsync();

    string WebRoot { get; }

    /// Post one JSON message to the page. Must be callable before init /
    /// after a crash (drops the message) and from the UI thread only.
    void PostJson(string json);

    /// Navigate to the app bundle (host knows the URL scheme).
    void NavigateToApp(bool disableWebgl);

    void NavigateToString(string html);
    void Reload();

    /// Raw JSON string from the page (WebMessageReceived). UI thread.
    event Action<string>? MessageReceived;

    event Action<WebViewFailure>? ProcessFailed;

    /// The browser process died: tear down and rebuild the whole control,
    /// then re-run init. The controller re-navigates afterwards.
    Task RecreateAsync();
}

/// Native-window services AppController needs from whatever hosts it.
internal interface IWindowHost
{
    /// Taskbar flash / dock bounce. Loud = agent blocked on the user (flash
    /// until foregrounded); gentle = one blink, skipped when foreground.
    void FlashAttention(bool loud);

    /// Current clipboard text, or null when unreadable (locked/empty).
    string? ReadClipboardText();

    /// Clipboard image (as PNG bytes) or text for a board paste. Null on a
    /// locked clipboard — the caller tells the user to retry.
    (byte[]? Png, string? Text)? ReadClipboardForBoard();

    /// Native folder picker; null on cancel.
    Task<string?> PickFolderAsync(string? initialDir);

    /// Native multi-select file picker; null/empty on cancel.
    Task<string[]?> PickFilesAsync(string? initialDir);
}

/// The URL-pane subsystem (real browser views floated over the page).
/// Windows: WebView2 controllers parented to the main HWND. macOS: WKWebView
/// subviews (or an inert stub until that lands).
internal interface IUrlPanes
{
    bool HasPanes { get; }
    void OnLayout(UrlPaneLayoutMsg msg);
    void OnDispose(PaneRef msg);
    void SetVisible(Guid paneId, bool visible);
    void SetSuppressed(bool suppressed);
    void CloseAll();
    string? UrlOf(Guid paneId);

    /// The pane navigated somewhere with a usable document title.
    event Action<Guid, string>? AutoTitleRequested;
    /// Policy rejected the URL (only web pages / local .html open in a pane).
    event Action<Guid, string>? Rejected;
    /// Navigation failed (status string) — surfaced only for file:// URLs.
    event Action<Guid, string>? Failed;
}

/// One native browser view floated over the page for a URL pane. Windows:
/// a WebView2 controller parented to the main HWND. macOS: a WKWebView
/// subview of the window's content view. Bounds arrive in page CSS pixels
/// relative to the main webview's viewport — each host converts to its own
/// coordinate space (DIP→device px + chrome offset on Windows; flipped
/// AppKit points on mac).
internal interface IUrlPaneHost
{
    void SetBounds(double x, double y, double w, double h);
    void SetVisible(bool visible);
    void NavigateIfChanged(string url);
    void Close();

    /// The document title changed (drives pane auto-naming). Optional — a
    /// host without title plumbing simply never raises it.
    event Action<string>? DocumentTitleChanged;

    /// Navigation completed unsuccessfully (status string).
    event Action<string>? NavigationFailed;
}

internal interface IUrlPaneHostFactory
{
    /// Create a native view for the pane, or null when the backing engine
    /// never became ready (the caller logs and gives up). The factory owns
    /// any wait-for-engine retry loop.
    Task<IUrlPaneHost?> CreateAsync(Guid paneId, string url, double x, double y, double w, double h);

    /// Forget any cached engine state (browser process died and is being
    /// rebuilt). Existing hosts are already closed by then.
    void Reset();
}

/// Auto-update. Windows: Velopack against the GitHub feed. macOS: stub until
/// a Sparkle-or-equivalent pipeline exists.
internal interface IUpdateService
{
    bool IsUpdatable { get; }
    string? CurrentVersion { get; }
    /// New version string when an update exists, else null/empty.
    Task<string?> CheckAsync();
    /// Download and restart into the new version. Does not return on success.
    Task DownloadAndApplyAsync();
}
