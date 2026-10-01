using Perch;
using Xunit;

namespace Perch.Tests;

/// The shims' dir goes first on the panes' PATH, whatever order a relaunch
/// inherited: behind the real `claude`, a tab's Claude ran without hooks.
public class ToolsPathTests
{
    private const string Tools = "/Applications/Perch.app/Contents/MacOS/tools";

    [Fact]
    public void AddedInFrontWhenMissing() =>
        Assert.Equal($"{Tools}:/Users/me/.local/bin:/usr/bin",
            ToolsPath.Front("/Users/me/.local/bin:/usr/bin", Tools, ':'));

    [Fact]
    public void MovedToTheFrontWhenARelaunchLeftItBehindTheLoginPath() =>
        // The updater's relaunch: login PATH merged ahead of the inherited one.
        Assert.Equal($"{Tools}:/Users/me/.local/bin:/opt/homebrew/bin:/usr/bin",
            ToolsPath.Front($"/Users/me/.local/bin:/opt/homebrew/bin:{Tools}:/usr/bin", Tools, ':'));

    [Fact]
    public void OnceOnly() =>
        Assert.Equal($"{Tools}:/usr/bin", ToolsPath.Front($"{Tools}:/usr/bin:{Tools}/", Tools, ':'));
}
