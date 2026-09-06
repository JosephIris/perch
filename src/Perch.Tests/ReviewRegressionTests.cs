using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Perch.Tests;

public sealed class ReviewRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "perch-regression-" + Guid.NewGuid().ToString("N"));
    public ReviewRegressionTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    [Fact]
    public void UnreadableTaskBoardAndExternalDeletionAreNotOverwritten()
    {
        var store = TeamStore.Open(_root) ?? TeamStore.Create(_root);
        Directory.CreateDirectory(store.Dir);
        File.WriteAllText(store.TasksPath, "{broken");
        _ = store.Tasks;
        store.SaveTasks();
        Assert.False(store.TasksReadable);
        Assert.Equal("{broken", File.ReadAllText(store.TasksPath));

        File.WriteAllText(store.TasksPath, "{}");
        store = TeamStore.Open(_root) ?? TeamStore.Create(_root);
        _ = store.Tasks;
        File.Delete(store.TasksPath);
        store.SaveTasks();
        Assert.False(File.Exists(store.TasksPath));
    }

    [Fact]
    public void TranscriptPartialLineIsRetriedAndOldRefusalIsIgnored()
    {
        var path = Path.Combine(_root, "transcript.jsonl");
        var now = DateTimeOffset.UtcNow;
        var row = $"{{\"timestamp\":\"{now:O}\",\"error\":\"rate_limit\",\"message\":\"You've reached your Opus limit\"}}\n";
        var watch = new ModelLimitWatch { Now = () => now };
        File.WriteAllText(path, row[..50]);
        Assert.False(watch.Scan(path, "opus"));
        File.AppendAllText(path, row[50..]);
        Assert.True(watch.Scan(path, "opus"));
        Assert.False(watch.Scan(path, "opus"));
        Assert.False(new ModelLimitWatch { Now = () => now.AddDays(2) }.Scan(path, "opus"));
    }

    [Fact]
    public void OutboxSurvivesReopeningWithoutReclassifyingUnknownSubmissionAsQueued()
    {
        var path = Path.Combine(_root, "outbox.json");
        var outbox = new TeamOutbox(path);
        outbox.Put(1, "ada", "first", "submitting");
        outbox.Put(2, "ada", "second", "queued");
        var reopened = new TeamOutbox(path);
        Assert.Equal(new[] { "submitting", "queued" }, reopened.Items.Select(i => i.State));
        reopened.Finish(1, "ada");
        Assert.Equal(2, new TeamOutbox(path).Items.Single().Seq);
    }

    [Fact]
    public async Task PendingNativePaneUsesLatestIntentAndOldGenerationCannotReplaceNew()
    {
        var factory = new Factory();
        var panes = new UrlPanes(new Ui(), factory);
        var id = Guid.NewGuid();
        UrlPaneLayoutMsg Layout(string url, double x) => new() { PaneId = id, Url = url, X = x, Y = 0, W = 100, H = 100 };
        panes.OnLayout(Layout("https://example.com", 1));
        panes.SetVisible(id, false);
        panes.OnLayout(Layout("https://example.org", 42));
        var host = new Host();
        factory.Pending[0].SetResult(host);
        await Task.Yield();
        Assert.False(host.Visible);
        Assert.Equal(42, host.X);
        Assert.Equal("https://example.org", host.Url);

        panes.OnDispose(new PaneRef { PaneId = id });
        panes.OnLayout(Layout("https://old.example", 1));
        panes.OnDispose(new PaneRef { PaneId = id });
        panes.OnLayout(Layout("https://new.example", 2));
        var old = new Host();
        factory.Pending[1].SetResult(old);
        var current = new Host();
        factory.Pending[2].SetResult(current);
        await Task.Yield();
        Assert.True(old.Closed);
        Assert.False(current.Closed);
        Assert.Equal("https://new.example", panes.UrlOf(id));
    }

    [Fact]
    public void RelativeWorktreePointerAndCommonRefsInvalidateNestedCwd()
    {
        var tree = Path.Combine(_root, "tree");
        var git = Path.Combine(_root, "common", "worktrees", "tree");
        var refs = Path.Combine(_root, "common", "refs", "remotes", "origin");
        Directory.CreateDirectory(Path.Combine(tree, "src"));
        Directory.CreateDirectory(git);
        Directory.CreateDirectory(refs);
        File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: ../common/worktrees/tree");
        File.WriteAllText(Path.Combine(git, "commondir"), "../..");
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main");
        var before = GitProc.RefreshSignature(tree);
        Assert.NotEmpty(before);
        Assert.Equal(before, GitProc.RefreshSignature(Path.Combine(tree, "src")));
        File.WriteAllText(Path.Combine(refs, "main"), new string('a', 40));
        Assert.NotEqual(before, GitProc.RefreshSignature(tree));
    }

    [Fact]
    public async Task InspectorDetailReusesUnchangedQueryResults()
    {
        foreach (var args in new[] { "init -q", "-c user.name=Test -c user.email=test@example.invalid -c commit.gpgsign=false commit --allow-empty -q -m baseline" })
        {
            var result = await ProcRunner.RunAsync("git", args, "test.setup", workingDir: _root, timeoutMs: 10000);
            Assert.Equal(0, result.Code);
        }
        var head = await ProcRunner.RunAsync("git", "rev-parse HEAD", "test.setup", workingDir: _root, timeoutMs: 10000);
        var empty = new HashSet<string>();
        using var scope = ProcRunner.BeginScope();
        var first = await GitProc.SessionDetailAsync(head.Stdout.Trim(), _root, empty, workingTreeFilter: empty);
        Assert.NotNull(first);
        var initialSpawns = scope.SpawnCount;
        await GitProc.SessionDetailAsync(head.Stdout.Trim(), _root, empty, workingTreeFilter: empty);
        await GitProc.SessionDetailAsync(head.Stdout.Trim(), _root, empty, workingTreeFilter: empty);
        Assert.Equal(initialSpawns, scope.SpawnCount);
        GitProc.InvalidateCache(_root);
        await GitProc.SessionDetailAsync(head.Stdout.Trim(), _root, empty, workingTreeFilter: empty);
        Assert.True(scope.SpawnCount > initialSpawns);
    }

    private sealed class Factory : IUrlPaneHostFactory
    {
        public readonly List<TaskCompletionSource<IUrlPaneHost?>> Pending = new();
        public Task<IUrlPaneHost?> CreateAsync(Guid id, string url, double x, double y, double w, double h)
        {
            var source = new TaskCompletionSource<IUrlPaneHost?>();
            Pending.Add(source);
            return source.Task;
        }
        public void Reset() { }
    }
    private sealed class Host : IUrlPaneHost
    {
        public bool Visible, Closed;
        public double X;
        public string? Url;
        public event Action<string>? DocumentTitleChanged { add { } remove { } }
        public event Action<string>? NavigationFailed { add { } remove { } }
        public void SetBounds(double x, double y, double w, double h) => X = x;
        public void SetVisible(bool visible) => Visible = visible;
        public void NavigateIfChanged(string url) => Url = url;
        public void Close() => Closed = true;
    }
    private sealed class Ui : IUiThread
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => throw new NotSupportedException();
    }
}
