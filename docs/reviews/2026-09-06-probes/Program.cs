// Observations for the 2026-09-06 review, NOT a passing regression suite.
// Each line reports current behavior; exit 0 means the probes completed.
// All files are synthetic and live under a unique temporary directory.
using Perch;
using System.Diagnostics;

if (args.Length == 2 && args[0] == "--stdin-child")
{
    File.WriteAllText(args[1], Environment.ProcessId.ToString());
    Thread.Sleep(10_000); // deliberately never reads redirected stdin
    return;
}

var scratch = Path.Combine(Path.GetTempPath(), "perch-review-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
Environment.SetEnvironmentVariable("PERCH_DATA_DIR", scratch);
Console.WriteLine($"Synthetic data: {scratch}");
TeamRoomProbes.Run(scratch);

// R01: A modern session title is mistaken for a legacy property name.
var store = SessionStore.Load();
store.Sessions[0].Title = "Panes";
store.Save();
var restored = SessionStore.Load();
Console.WriteLine($"R01 title: expected Panes; actual {restored.Sessions[0].Title}; file exists={File.Exists(Path.Combine(scratch, "perch", "sessions.json"))}");

// R04: Intent received during asynchronous native-view creation is lost.
var factory = new DeferredFactory();
var registry = new UrlPanes(new InlineUi(), factory);
var id = Guid.NewGuid();
registry.OnLayout(new UrlPaneLayoutMsg { PaneId = id, Url = "https://example.com/old", X = 0, Y = 0, W = 100, H = 100 });
registry.SetVisible(id, false);
registry.OnLayout(new UrlPaneLayoutMsg { PaneId = id, Url = "https://example.com/new", X = 0, Y = 0, W = 200, H = 200 });
var native = new FakeUrlHost();
factory.Pending[0].SetResult(native);
Console.WriteLine($"R04 pending create: expected visible=False, url=/new, bounds updated; actual visible={native.Visible}, url={registry.UrlOf(id)}, bounds calls={native.BoundsCalls}");
registry.CloseAll();

// R04: An old completion mistakes a newer create's ID for its own generation.
factory = new DeferredFactory();
registry = new UrlPanes(new InlineUi(), factory);
registry.OnLayout(new UrlPaneLayoutMsg { PaneId = id, Url = "https://example.com/old", X = 0, Y = 0, W = 100, H = 100 });
registry.OnDispose(new PaneRef { PaneId = id });
registry.OnLayout(new UrlPaneLayoutMsg { PaneId = id, Url = "https://example.com/new", X = 0, Y = 0, W = 100, H = 100 });
var oldNative = new FakeUrlHost();
var newNative = new FakeUrlHost();
factory.Pending[0].SetResult(oldNative);
factory.Pending[1].SetResult(newNative);
Console.WriteLine($"R04 reused ID: expected old closed=True, new closed=False; actual old closed={oldNative.Closed}, new closed={newNative.Closed}, url={registry.UrlOf(id)}");
registry.CloseAll();

// R08: First scan treats an old refusal as a fresh one, ignoring its timestamp.
var transcript = Path.Combine(scratch, "old.jsonl");
var refusal = "{\"timestamp\":\"2020-01-01T00:00:00Z\",\"error\":\"rate_limit\",\"message\":\"You've reached your Opus limit\"}\n";
File.WriteAllText(transcript, refusal);
var watch = new ModelLimitWatch();
Console.WriteLine($"R08 old refusal: expected False; actual {watch.Scan(transcript, "opus")}");

// R08: An incomplete JSONL row is consumed rather than retained for next scan.
transcript = Path.Combine(scratch, "partial.jsonl");
refusal = refusal.Replace("2020-01-01T00:00:00Z", DateTimeOffset.UtcNow.ToString("O"));
var split = refusal.IndexOf("rate_limit", StringComparison.Ordinal) + 4;
File.WriteAllText(transcript, refusal[..split]);
watch = new ModelLimitWatch();
watch.Scan(transcript, "opus");
File.AppendAllText(transcript, refusal[split..]);
Console.WriteLine($"R08 split refusal: expected True; actual {watch.Scan(transcript, "opus")}");

// R07: A worktree fingerprint doesn't include its common-directory refs.
var tree = Path.Combine(scratch, "tree");
var common = Path.Combine(scratch, "common");
var gitdir = Path.Combine(common, "worktrees", "tree");
Directory.CreateDirectory(tree);
Directory.CreateDirectory(gitdir);
Directory.CreateDirectory(Path.Combine(common, "refs", "remotes", "origin"));
File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: " + gitdir);
File.WriteAllText(Path.Combine(gitdir, "HEAD"), "ref: refs/heads/main\n");
File.WriteAllText(Path.Combine(gitdir, "commondir"), "../..\n");
var remoteRef = Path.Combine(common, "refs", "remotes", "origin", "main");
File.WriteAllText(remoteRef, new string('a', 40));
var before = GitProc.RefreshSignature(tree);
File.WriteAllText(remoteRef, new string('b', 40));
File.SetLastWriteTimeUtc(remoteRef, DateTime.UtcNow.AddSeconds(2));
Console.WriteLine($"R07 common ref changed: expected fingerprint changed=True; actual {before != GitProc.RefreshSignature(tree)}");
Directory.CreateDirectory(Path.Combine(tree, "src"));
Console.WriteLine($"R07 nested cwd: expected nonempty fingerprint; actual length={GitProc.RefreshSignature(Path.Combine(tree, "src")).Length}");

// R11: Inspector's repeated detail reads bypass the status-refresh cache.
var repo = Path.Combine(scratch, "inspector-repo");
Directory.CreateDirectory(repo);
foreach (var command in new[] { "init -q", "-c user.name=Review -c user.email=review@example.invalid -c commit.gpgsign=false commit --allow-empty -q -m baseline" })
{
    var result = await ProcRunner.RunAsync("git", command, "review.setup", workingDir: repo, timeoutMs: 5000);
    if (result.Code != 0) throw new Exception(result.Stderr);
}
var head = await ProcRunner.RunAsync("git", "rev-parse HEAD", "review.setup", workingDir: repo, timeoutMs: 5000);
var empty = new HashSet<string>();
using (var scope = ProcRunner.BeginScope())
{
    for (var i = 0; i < 3; i++) await GitProc.SessionDetailAsync(head.Stdout.Trim(), repo, empty, workingTreeFilter: empty);
    Console.WriteLine($"R11 unchanged inspector detail: requests=3, process launches={scope.SpawnCount}");
}

// R09: The subprocess timeout starts only AFTER stdin has finished writing.
var pidFile = Path.Combine(scratch, "child.pid");
var exe = Environment.ProcessPath!;
var childArgs = $"--stdin-child \"{pidFile}\"";
if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    childArgs = $"\"{System.Reflection.Assembly.GetExecutingAssembly().Location}\" " + childArgs;
var run = ProcRunner.RunAsync(exe, childArgs, "review.stdin", timeoutMs: 200, stdinText: new string('x', 2 * 1024 * 1024));
try
{
    await Task.WhenAny(run, Task.Delay(1200));
    Console.WriteLine($"R09 200ms timeout with blocked stdin: expected completed=True by 1200ms; actual {run.IsCompleted}");
}
finally
{
    // Terminate only this probe's child, whose PID it wrote in our unique dir.
    if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out var childPid))
    {
        try { using var child = Process.GetProcessById(childPid); child.Kill(entireProcessTree: true); }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }
    await run.WaitAsync(TimeSpan.FromSeconds(15));
}

sealed class DeferredFactory : IUrlPaneHostFactory
{
    public List<TaskCompletionSource<IUrlPaneHost?>> Pending = new();
    public Task<IUrlPaneHost?> CreateAsync(Guid paneId, string url, double x, double y, double w, double h)
    {
        var completion = new TaskCompletionSource<IUrlPaneHost?>();
        Pending.Add(completion);
        return completion.Task;
    }
    public void Reset() { }
}

sealed class FakeUrlHost : IUrlPaneHost
{
    public bool Visible;
    public bool Closed;
    public int BoundsCalls;
    public event Action<string>? DocumentTitleChanged { add { } remove { } }
    public event Action<string>? NavigationFailed { add { } remove { } }
    public void SetBounds(double x, double y, double w, double h) => BoundsCalls++;
    public void SetVisible(bool visible) => Visible = visible;
    public void NavigateIfChanged(string url) { }
    public void Close() => Closed = true;
}

sealed class InlineUi : IUiThread
{
    public void Post(Action action) => action();
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => throw new NotSupportedException();
}
