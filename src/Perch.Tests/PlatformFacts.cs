using System;
using Xunit;

namespace Perch.Tests;

/// A [Fact] whose fixtures encode Windows path/shell semantics (drive
/// letters, backslash separators, case-insensitive compare, pwsh splicing).
/// On other OSes those inputs aren't rooted paths at all, so the test would
/// assert the wrong thing rather than test it — skip instead. Each use should
/// have a unix-fixture mirror alongside it.
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows path/shell semantics — mirrored by a unix-fixture test";
    }
}

/// The inverse: fixtures that need '/'-rooted paths to be the native form.
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix path semantics — mirrored by a Windows-fixture test";
    }
}
