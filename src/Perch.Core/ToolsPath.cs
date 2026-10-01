using System;
using System.Linq;

namespace Perch;

/// The bundled tools dir (the perch CLI and the claude/codex shims) must be
/// FIRST on the PATH every pane inherits: the shim is what gives a pane's
/// Claude its hooks, and with the real `claude` ahead of it a tab's Claude
/// runs unseen (no state, no badge; dictation typed but never sent; a
/// permission prompt the phone never heard of).
///
/// "Add it if it's missing" was not enough on the mac. A relaunch that
/// inherits the previous PATH (the updater restarting Perch, or Perch opened
/// from one of its own panes) already has the dir, and the login shell's PATH
/// is merged in FRONT of the inherited one (MacShellEnv), so the dir ended up
/// last and the check saw it present and left it there.
internal static class ToolsPath
{
    /// `path` with `toolsDir` moved (or added) to the front, once.
    public static string Front(string path, string toolsDir, char separator)
    {
        var rest = path.Split(separator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !string.Equals(p.TrimEnd('/', '\\'), toolsDir.TrimEnd('/', '\\'), StringComparison.Ordinal));
        return string.Join(separator, new[] { toolsDir }.Concat(rest));
    }
}
