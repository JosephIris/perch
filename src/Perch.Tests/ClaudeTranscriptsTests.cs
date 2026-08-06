using Perch;
using Xunit;

namespace Perch.Tests;

/// The resume pre-flight looks for `~/.claude/projects/<sanitized-cwd>/<id>.jsonl`
/// before arming `claude --resume <id>`. The sanitizer has to reproduce Claude's
/// own rule exactly, because a miss doesn't error — it falls through to a
/// recursive scan of every project dir, which is slow and will happily match a
/// transcript belonging to a DIFFERENT project. `claude --resume` scopes to the
/// directory it runs in, so that match arms a resume that dies on launch with
/// "No conversation found" and leaves a bare shell in the right folder.
///
/// The expected values below are real directory names observed under
/// ~/.claude/projects.
public class ClaudeTranscriptsTests
{
    [Theory]
    [InlineData(@"C:\Users\josep\dev-projects\cmux-win", "C--Users-josep-dev-projects-cmux-win")]
    // The regression: '_' is not a path separator, but Claude still folds it.
    [InlineData(@"c:\Users\josep\repos\global_dnn", "c--Users-josep-repos-global-dnn")]
    // ...and so is a dot, which is how a worktree under `.claude` files itself.
    [InlineData(@"C:\Users\josep\dev-projects\cmux-win\.claude\worktrees\perf",
                "C--Users-josep-dev-projects-cmux-win--claude-worktrees-perf")]
    [InlineData("/home/josep/repos/thing", "-home-josep-repos-thing")]
    public void MatchesClaudesProjectDirKey(string cwd, string expected)
        => Assert.Equal(expected, ClaudeTranscripts.SanitizeCwd(cwd));

    /// Anything that isn't an ASCII letter or digit is a separator — spaces and
    /// the rest of the punctuation included.
    [Fact]
    public void FoldsEveryNonAlphanumeric()
        => Assert.Equal("C--My-Repo--v2-", ClaudeTranscripts.SanitizeCwd(@"C:\My Repo (v2)"));

    /// Dashes are already the separator, so they survive untouched — otherwise
    /// every hyphenated folder name would double up and never match.
    [Fact]
    public void LeavesExistingDashesAlone()
        => Assert.Equal("dev-projects-cmux-win", ClaudeTranscripts.SanitizeCwd(@"dev-projects\cmux-win"));
}
