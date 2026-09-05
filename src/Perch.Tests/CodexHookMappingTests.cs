using PerchCli;
using Xunit;

namespace Perch.Tests;

/// Codex's editing tool, read the way the hook actually receives it.
///
/// The patch text below is copied verbatim from a PreToolUse payload captured
/// with PERCH_HOOK_DUMP against codex-cli 0.153.2. It is the only place a codex
/// file edit names its files, so this parse is what feeds both the activity
/// line ("editing notes.md") and the per-pane line counts.
public class CodexHookMappingTests
{
    private const string RealPatch =
        @"*** Begin Patch
*** Update File: C:\tmp\codex-lab\notes.md
@@
+line two
*** End Patch";

    [Fact]
    public void APatchNamesTheFileItEdits()
        => Assert.Equal(new[] { @"C:\tmp\codex-lab\notes.md" }, CodexPatch.Files(RealPatch));

    [Fact]
    public void EveryKindOfChangeIsClaimed()
    {
        const string patch = @"*** Begin Patch
*** Add File: a.txt
+hello
*** Update File: b.txt
@@
-x
+y
*** Delete File: c.txt
*** End Patch";
        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, CodexPatch.Files(patch));
    }

    [Fact]
    public void ARenameClaimsWhereTheFileEndsUp()
    {
        const string patch = @"*** Begin Patch
*** Update File: old.txt
*** Move to: new.txt
@@
*** End Patch";
        Assert.Equal(new[] { "old.txt", "new.txt" }, CodexPatch.Files(patch));
    }

    [Fact]
    public void OneFileTouchedTwiceIsClaimedOnce()
    {
        const string patch = @"*** Begin Patch
*** Update File: a.txt
@@
*** Update File: a.txt
@@
*** End Patch";
        Assert.Single(CodexPatch.Files(patch));
    }

    [Fact]
    public void ContentThatLooksLikeAHeaderIsNotOne()
    {
        // A patch that ADDS a line mentioning the header syntax must not have
        // that line read as a header — the marker is the line's own prefix.
        const string patch = @"*** Begin Patch
*** Update File: doc.md
@@
+see *** Add File: fake.txt
*** End Patch";
        Assert.Equal(new[] { "doc.md" }, CodexPatch.Files(patch));
    }

    [Fact]
    public void NothingToParseIsNotAFailure()
    {
        Assert.Empty(CodexPatch.Files(""));
        Assert.Empty(CodexPatch.Files(null));
        Assert.Empty(CodexPatch.Files("just some text"));
    }
}
