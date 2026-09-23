using Xunit;

namespace Perch.Tests;

// A project chat's shared memory and what its prompts carry. The memory file
// is Perch's (the coordinator and threads write it through `perch thread
// remember`), so round-tripping it — and ignoring anything that isn't a note —
// is what keeps the notes every new thread reads intact.
public class ThreadMemoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "perch-mem-" + Guid.NewGuid().ToString("N"));
    private readonly string? _prev = Environment.GetEnvironmentVariable("PERCH_DATA_DIR");

    public ThreadMemoryTests() => Environment.SetEnvironmentVariable("PERCH_DATA_DIR", _dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PERCH_DATA_DIR", _prev);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Memory_RoundTrips_AndIgnoresOtherLines()
    {
        var lead = new Session { IsLead = true };
        Assert.Empty(ThreadController.ReadMemory(lead));
        ThreadController.WriteMemory(lead, new List<string> { "Release moved to Friday", "Ask before\ntouching billing" });
        var back = ThreadController.ReadMemory(lead);
        Assert.Equal(new[] { "Release moved to Friday", "Ask before touching billing" }, back);
        File.AppendAllText(Path.Combine(ThreadController.DirFor(lead), "memory.md"), "\nnot a note\n- \n");
        Assert.Equal(2, ThreadController.ReadMemory(lead).Count);
    }

    [Fact]
    public void Prompts_CarryGoalInstructionsAndMemory_OnlyWhenSet()
    {
        var proj = new Project { Name = "demo", Path = @"C:\demo" };
        var bare = ThreadController.CoordinatorPrompt(proj);
        Assert.DoesNotContain("## The goal", bare);
        Assert.DoesNotContain("## Project memory", bare);
        Assert.Contains("perch thread suggest", bare);

        var full = ThreadController.CoordinatorPrompt(proj, "Ship export", "Use the develop branch", new[] { "Friday release" });
        Assert.Contains("## The goal\nShip export", full.Replace("\r\n", "\n"));
        Assert.Contains("Use the develop branch", full);
        Assert.Contains("1. Friday release", full);

        var thread = ThreadController.ThreadPrompt(2, "Tests", "Write tests", "Use the develop branch", new[] { "Friday release" });
        Assert.Contains("Use the develop branch", thread);
        Assert.Contains("1. Friday release", thread);
        Assert.Contains("perch thread remember", thread);
    }
}
