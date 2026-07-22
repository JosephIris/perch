using System.Collections.Generic;
using Perch;
using Xunit;

namespace Perch.Tests;

/// The per-tab "↑N mine" split: a pane's hook-claimed short shas intersected
/// with the repo's unpushed set. The interesting edges are prefix matching
/// (the hook records the SHORT sha `git commit` printed), claims that stop
/// mattering (pushed / rebased away), and one full sha never being counted
/// twice even if claims overlap.
public class GitAttributionTests
{
    private static readonly IReadOnlySet<string> Unpushed = new HashSet<string>(
        new[]
        {
            "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
            "b2c3d4e5f60718293a4b5c6d7e8f90123456789a",
            "c3d4e5f60718293a4b5c6d7e8f90123456789ab0",
        },
        System.StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ShortShaClaimsMatchByPrefix()
    {
        Assert.Equal(2, GitProc.CountAttributed(Unpushed, new[] { "a1b2c3d", "c3d4e5f6" }));
    }

    [Fact]
    public void PushedOrRebasedClaimsStopMatching()
    {
        // A sha no longer in @{upstream}..HEAD simply contributes nothing.
        Assert.Equal(0, GitProc.CountAttributed(Unpushed, new[] { "deadbeef", "0123456" }));
    }

    [Fact]
    public void OneCommitNeverCountsTwice()
    {
        // Two claims prefixing the SAME full sha (7-char and 12-char record of
        // one commit) must count it once.
        Assert.Equal(1, GitProc.CountAttributed(Unpushed, new[] { "a1b2c3d", "a1b2c3d4e5f6" }));
    }

    [Fact]
    public void TinyFragmentsAreIgnored()
    {
        // Below 7 hex chars a prefix is too collision-prone to trust.
        Assert.Equal(0, GitProc.CountAttributed(Unpushed, new[] { "a1b2", "" }));
    }

    [Fact]
    public void CaseInsensitive()
    {
        Assert.Equal(1, GitProc.CountAttributed(Unpushed, new[] { "A1B2C3D4" }));
    }
}
