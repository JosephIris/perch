using System;
using System.IO;

namespace Perch;

/// Per-pane Claude Code model selection, persisted to a tiny temp file that the
/// `wrap-claude` shim reads at launch. The pane's PERCH_PANE_ID env var is
/// frozen when the shell spawns, but the user can change the model AFTER that —
/// so the selection can't ride an env var. Instead the host writes the current
/// alias to %TEMP%\perch-claude-model-&lt;paneId&gt;.txt whenever it changes (and
/// again at spawn), and wrap-claude re-reads that file every time `claude` is
/// invoked, appending `--model &lt;alias&gt;` when it's set.
///
/// This deliberately mirrors the existing per-pane hooks temp file (see
/// ClaudeWrapper.WriteHooksFile → perch-claude-hooks-&lt;paneId&gt;.json) and needs
/// no new IPC direction: the per-pane pipe is one-way (the host only reads it),
/// so making wrap-claude QUERY the host for the model would mean a bidirectional
/// pipe — a far larger change for the same result.
internal static class ClaudeModelState
{
    /// The path wrap-claude reads. Keyed by the pane id in "N" format to match
    /// PERCH_PANE_ID (Shell.BuildStartupCommandLine sets it as paneId.ToString("N")).
    public static string PathFor(Guid paneId)
        => Path.Combine(Path.GetTempPath(), $"perch-claude-model-{paneId:N}.txt");

    /// Write the alias, or clear it (delete the file) for empty/whitespace →
    /// account default. Never throws — a model pick must never break the pane.
    public static void Write(Guid paneId, string? alias)
    {
        try
        {
            var path = PathFor(paneId);
            var a = (alias ?? "").Trim();
            if (a.Length == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            File.WriteAllText(path, a);
        }
        catch (Exception ex) { Log.Info("ClaudeModelState.Write", $"skipped: {ex.Message}"); }
    }
}
