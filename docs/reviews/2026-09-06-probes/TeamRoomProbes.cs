using Perch;
using System.Collections;
using System.Reflection;
using System.Text.Json;

internal static class TeamRoomProbes
{
    public static void Run(string scratch)
    {
        var h = new Fixture(scratch, "burst", running: true);
        var session = h.Sessions.Single();
        session.Root.AgentState = AgentState.Working;
        h.Post(h.Projects[0], "first");
        h.Post(h.Projects[0], "second");
        session.Root.AgentState = AgentState.Done;
        h.Controller.OnAgentStatus(session, new StatusMessage("done", null));
        var flush = h.Delayed.First(x => x.Delay == TimeSpan.FromSeconds(1));
        flush.Action();
        Console.WriteLine($"T01 parked burst: typed before any ACK={h.Typed.Count}, tracked submissions={Count(h.Controller, "_submits")}");

        h = new Fixture(scratch, "ack", running: true);
        session = h.Sessions.Single();
        h.Post(h.Projects[0], "first");
        var first = h.Controller.StoreFor(h.Projects[0].Id)!.Ledger.ReadAll().Last(e => e.Kind == "user");
        h.Post(h.Projects[0], "second");
        h.Controller.OnAgentStatus(session, new StatusMessage("working", TeamController.DeliveryLine("first", "Ada", first.Seq)));
        Console.WriteLine($"T02 ACK for first post: expected second remains queued; queued sessions={Count(h.Controller, "_parked")}, unacknowledged submissions={Count(h.Controller, "_submits")}");

        h = new Fixture(scratch, "projects", running: false);
        h.AddProject(Path.Combine(scratch, "projects-second"), running: false);
        h.Post(h.Projects[0], "project one private task");
        h.Post(h.Projects[1], "project two private task");
        var queues = (IDictionary)Field(h.Controller, "_pendingStart");
        var queued = queues.Values.Cast<ICollection>().Sum(v => v.Count);
        Console.WriteLine($"T03 two projects with Ada: expected 2 starts/2 queues; actual starts={h.Starts}, queues={queues.Count}, total queued posts={queued}");

        var taskStore = TeamStore.Create(Path.Combine(scratch, "broken-tasks"));
        File.WriteAllText(taskStore.TasksPath, "{ broken JSON: preserve me");
        _ = taskStore.Tasks;
        taskStore.SaveTasks();
        Console.WriteLine($"T04 corrupt tasks: expected original preserved; actual preserved={File.ReadAllText(taskStore.TasksPath).Contains("preserve me")}, team readable={taskStore.Readable}");

        h = new Fixture(scratch, "retry", running: true);
        session = h.Sessions.Single();
        var picture = Path.Combine(scratch, "attachment.png");
        File.WriteAllBytes(picture, Array.Empty<byte>()); // only path/existence is used by this delivery probe
        h.Post(h.Projects[0], "look at the picture", picture);
        var post = h.Controller.StoreFor(h.Projects[0].Id)!.Ledger.ReadAll().Last(e => e.Kind == "user");
        h.Typed.Clear();
        session.Root.AgentState = AgentState.Working;
        h.Controller.OnDeliverRetry(new TeamDeliverRetryMsg { ProjectId = h.Projects[0].Id, BotId = "ada", Seq = post.Seq });
        Console.WriteLine($"T05 retry while busy: expected parked; actual typed={h.Typed.Count}, attachment retained={h.Controller.StoreFor(h.Projects[0].Id)!.Outbox.Items.Any(d => d.Line.Contains(picture))}");
    }

    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static int Count(object target, string name) => ((IDictionary)Field(target, name)).Count;

    private sealed class Fixture
    {
        public List<Project> Projects = new();
        public List<Session> Sessions = new();
        public List<string> Typed = new();
        public List<(Action Action, TimeSpan Delay)> Delayed = new();
        public int Starts;
        public TeamController Controller;

        public Fixture(string scratch, string name, bool running)
        {
            AddProject(Path.Combine(scratch, name), running);
            Controller = new TeamController(new TeamHost
            {
                ProjectById = id => Projects.FirstOrDefault(p => p.Id == id),
                Projects = () => Projects,
                SessionById = id => Sessions.FirstOrDefault(s => s.Id == id),
                Sessions = () => Sessions,
                ResolveCwd = (s, _) => s.Cwd,
                ReadTranscript = (_, _, _) => null,
                TypeToClaude = (_, text) => { Typed.Add(text); return true; },
                PressEnter = _ => true,
                Wake = s => s.Dormant = false,
                // Simulate a slow worktree/start; never launch an actual process.
                CreateTab = (_, _, _, _, _) => { Starts++; return new TaskCompletionSource<Session?>().Task; },
                CloseSession = (_, _) => { },
                Post = _ => { }, PushState = () => { },
                Delay = (a, t) => Delayed.Add((a, t)),
                WriteRaw = (_, _) => { }, HasPty = _ => true,
            });
        }

        public void AddProject(string path, bool running)
        {
            Directory.CreateDirectory(path);
            var project = new Project { Name = Path.GetFileName(path), Path = path };
            Projects.Add(project);
            var store = TeamStore.Create(path);
            store.Doc.Positions.Add(new TeamPosition { Slug = "dev", Name = "Developer", Purpose = "Test" });
            var bot = new TeamBot { Slug = "ada", Nickname = "Ada", PositionSlug = "dev", CcName = "ada" };
            if (running)
            {
                var session = new Session { ProjectId = project.Id, Cwd = path };
                session.Root.AgentType = "claude";
                session.Root.AgentState = AgentState.Done;
                session.Root.ClaudeSessionId = Guid.NewGuid().ToString();
                bot.SessionId = session.Id;
                Sessions.Add(session);
            }
            store.Doc.Bots.Add(bot);
            store.Save();
        }

        public void Post(Project project, string text, string? image = null) => Controller.OnPost(new TeamPostMsg
        {
            ProjectId = project.Id, Text = text, Image = image, ClientId = Guid.NewGuid().ToString(),
            To = JsonSerializer.SerializeToElement(new[] { "Ada" }),
        });
    }
}
