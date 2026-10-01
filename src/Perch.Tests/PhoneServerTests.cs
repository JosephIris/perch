using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Perch;
using Xunit;

namespace Perch.Tests;

/// The phone link over a real socket: nothing answers without the pairing
/// token, a guessing address is shut out, and the three things the phone
/// does (list, send, read the answer) reach the host.
public class PhoneServerTests
{
    private sealed class InlineUi : IUiThread
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => throw new NotSupportedException();
    }

    private const string Token = "secret-token";
    private static readonly Guid Tab = Guid.NewGuid();
    private static readonly Guid Proj = Guid.NewGuid();
    private static readonly Guid NewTab = Guid.NewGuid();

    private sealed class Fixture : IDisposable
    {
        public readonly List<(Guid Id, string Text)> Sent = new();
        public readonly List<PhoneNewTab> Created = new();
        public readonly List<(string Answer, string? Text)> Answers = new();
        public bool Asking = true;
        public readonly PhoneServer Server;
        public readonly HttpClient Http = new();

        public Fixture()
        {
            Server = new PhoneServer(new InlineUi(), new PhoneServer.Host
            {
                Sessions = () => new[] { new PhoneSession(Tab, "fix login", "perch", "claude", "done", true, true, false, 3) },
                History = id => Task.FromResult<IReadOnlyList<PhoneHistoryItem>?>(id != Tab ? null : new[]
                {
                    new PhoneHistoryItem("user", "run the tests", 1000),
                    new PhoneHistoryItem("tool", "Run dotnet test ×2", null),
                    new PhoneHistoryItem("claude", "All tests pass.", 2000),
                }),
                Projects = () => new[] { new PhoneProject(Proj, "perch") },
                Ask = id => id == Tab && Asking
                    ? new PhoneAsk("Bash", "git push origin main", "{\"command\":\"git push origin main\"}", new[] { "Bash(git push:*)" }, true)
                    : null,
                Answer = (id, answer, text) =>
                {
                    if (id != Tab) return "missing";
                    if (!Asking) return "not-asking";
                    Answers.Add((answer, text));
                    Asking = false;
                    return "answered";
                },
                Create = tab =>
                {
                    if (tab.ProjectId != Proj) return Task.FromResult<Guid?>(null);
                    Created.Add(tab);
                    return Task.FromResult<Guid?>(NewTab);
                },
                Send = (id, text) =>
                {
                    if (id != Tab) return "missing";
                    Sent.Add((id, text));
                    return "queued";
                },
                Reply = id => Task.FromResult<PhoneReply?>(id == Tab ? new PhoneReply("All tests pass.", null) : null),
                Token = () => Token,
                Name = () => "TESTPC",
            });
            Server.Start(0);
            Http.BaseAddress = new Uri($"http://127.0.0.1:{Server.Port}/");
        }

        public HttpRequestMessage Req(HttpMethod m, string path, string? token = Token, object? body = null)
        {
            var r = new HttpRequestMessage(m, path);
            if (token != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return r;
        }

        public void Dispose() { Http.Dispose(); Server.Dispose(); }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task NothingAnswersWithoutTheToken()
    {
        using var f = new Fixture();
        var none = await f.Http.SendAsync(f.Req(HttpMethod.Get, "v1/sessions", token: null));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        var wrong = await f.Http.SendAsync(f.Req(HttpMethod.Get, "v1/sessions", token: "nope"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var send = await f.Http.SendAsync(f.Req(HttpMethod.Post, $"v1/sessions/{Tab}/send", token: "nope", body: new { text = "rm -rf" }));
        Assert.Equal(HttpStatusCode.Unauthorized, send.StatusCode);
        Assert.Empty(f.Sent);
    }

    [Fact]
    public async Task AnAddressThatKeepsGuessingIsShutOut()
    {
        using var f = new Fixture();
        for (int i = 0; i < 10; i++)
            await f.Http.SendAsync(f.Req(HttpMethod.Get, "v1/hello", token: "guess" + i));
        // Even the right token waits out the minute.
        var r = await f.Http.SendAsync(f.Req(HttpMethod.Get, "v1/hello"));
        Assert.Equal((HttpStatusCode)429, r.StatusCode);
    }

    [Fact]
    public async Task HelloAndTheSessionList()
    {
        using var f = new Fixture();
        var hello = await Json(await f.Http.SendAsync(f.Req(HttpMethod.Get, "v1/hello")));
        Assert.Equal("perch", hello.GetProperty("app").GetString());
        Assert.Equal(PhoneServer.ApiVersion, hello.GetProperty("api").GetInt32());
        Assert.Equal("TESTPC", hello.GetProperty("name").GetString());

        var list = await Json(await f.Http.SendAsync(f.Req(HttpMethod.Get, "v1/sessions")));
        var s = list.GetProperty("sessions")[0];
        Assert.Equal(Tab, s.GetProperty("id").GetGuid());
        Assert.Equal("fix login", s.GetProperty("title").GetString());
        Assert.Equal("perch", s.GetProperty("project").GetString());
        Assert.Equal("claude", s.GetProperty("kind").GetString());
        Assert.Equal("done", s.GetProperty("state").GetString());
        Assert.True(s.GetProperty("canSend").GetBoolean());
        Assert.Equal(3, s.GetProperty("color").GetInt32());
    }

    [Fact]
    public async Task ASessionsConversationComesBackOldestFirst()
    {
        using var f = new Fixture();
        var h = await Json(await f.Http.SendAsync(f.Req(HttpMethod.Get, $"v1/sessions/{Tab}/history")));
        var items = h.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal("user", items[0].GetProperty("kind").GetString());
        Assert.Equal("run the tests", items[0].GetProperty("text").GetString());
        Assert.Equal(1000, items[0].GetProperty("atMs").GetInt64());
        Assert.Equal("tool", items[1].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("atMs").ValueKind);
        Assert.Equal("claude", items[2].GetProperty("kind").GetString());

        var gone = await f.Http.SendAsync(f.Req(HttpMethod.Get, $"v1/sessions/{Guid.NewGuid()}/history"));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task APermissionPromptIsShownAndAnswered()
    {
        using var f = new Fixture();
        var ask = await Json(await f.Http.SendAsync(f.Req(HttpMethod.Get, $"v1/sessions/{Tab}/permission")));
        Assert.True(ask.GetProperty("asking").GetBoolean());
        Assert.Equal("Bash", ask.GetProperty("tool").GetString());
        Assert.Equal("git push origin main", ask.GetProperty("summary").GetString());
        Assert.True(ask.GetProperty("canAlways").GetBoolean());
        Assert.Equal("Bash(git push:*)", ask.GetProperty("rules")[0].GetString());

        var bad = await f.Http.SendAsync(f.Req(HttpMethod.Post, $"v1/sessions/{Tab}/permission", body: new { answer = "maybe" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var r = await Json(await f.Http.SendAsync(f.Req(HttpMethod.Post, $"v1/sessions/{Tab}/permission",
            body: new { answer = "deny", text = "  push to a branch instead  " })));
        Assert.Equal("answered", r.GetProperty("result").GetString());
        Assert.Equal(("deny", (string?)"push to a branch instead"), Assert.Single(f.Answers));

        // Answered: nothing to show, and a second answer changes nothing.
        var after = await Json(await f.Http.SendAsync(f.Req(HttpMethod.Get, $"v1/sessions/{Tab}/permission")));
        Assert.False(after.GetProperty("asking").GetBoolean());
        var again = await Json(await f.Http.SendAsync(f.Req(HttpMethod.Post, $"v1/sessions/{Tab}/permission", body: new { answer = "allow" })));
        Assert.Equal("not-asking", again.GetProperty("result").GetString());

        var gone = await f.Http.SendAsync(f.Req(HttpMethod.Post, $"v1/sessions/{Guid.NewGuid()}/permission", body: new { answer = "allow" }));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task ANewTabOpensInAProjectWithItsFirstMessage()
    {
        using var f = new Fixture();
        var projects = await Json(await f.Http.SendAsync(f.Req(HttpMethod.Get, "v1/projects")));
        Assert.Equal(Proj, projects.GetProperty("projects")[0].GetProperty("id").GetGuid());
        Assert.Equal("perch", projects.GetProperty("projects")[0].GetProperty("name").GetString());

        var made = await f.Http.SendAsync(f.Req(HttpMethod.Post, "v1/sessions",
            body: new { projectId = Proj, name = " login bug ", text = "look at the login page", voice = true }));
        var r = await Json(made);
        Assert.Equal("created", r.GetProperty("result").GetString());
        Assert.Equal(NewTab, r.GetProperty("id").GetGuid());
        var tab = Assert.Single(f.Created);
        Assert.Equal("login bug", tab.Name);
        Assert.Equal("look at the login page " + PhoneServer.VoiceTag, tab.Text);

        await f.Http.SendAsync(f.Req(HttpMethod.Post, "v1/sessions", body: new { projectId = Proj }));
        Assert.Null(f.Created[1].Text);   // no first message: Claude just starts

        var unknown = await f.Http.SendAsync(f.Req(HttpMethod.Post, "v1/sessions", body: new { projectId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var bad = await f.Http.SendAsync(f.Req(HttpMethod.Post, "v1/sessions", body: new { name = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(2, f.Created.Count);
    }

    [Fact]
    public async Task ALineReachesItsSessionTaggedWhenSpoken()
    {
        using var f = new Fixture();
        var typed = await f.Http.SendAsync(f.Req(HttpMethod.Post, $"v1/sessions/{Tab}/send", body: new { text = "  run the tests  " }));
        Assert.Equal("queued", (await Json(typed)).GetProperty("result").GetString());
        await f.Http.SendAsync(f.Req(HttpMethod.Post, $"v1/sessions/{Tab}/send", body: new { text = "and push", voice = true }));
        Assert.Equal(new[] { "run the tests", "and push " + PhoneServer.VoiceTag }, f.Sent.ConvertAll(x => x.Text));

        var gone = await f.Http.SendAsync(f.Req(HttpMethod.Post, $"v1/sessions/{Guid.NewGuid()}/send", body: new { text = "hi" }));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task TheLatestAnswerComesBack()
    {
        using var f = new Fixture();
        var r = await Json(await f.Http.SendAsync(f.Req(HttpMethod.Get, $"v1/sessions/{Tab}/reply")));
        Assert.Equal("All tests pass.", r.GetProperty("text").GetString());
        var none = await Json(await f.Http.SendAsync(f.Req(HttpMethod.Get, $"v1/sessions/{Guid.NewGuid()}/reply")));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("text").ValueKind);
    }

    [Fact]
    public async Task AnUnknownPathIs404()
    {
        using var f = new Fixture();
        var r = await f.Http.SendAsync(f.Req(HttpMethod.Get, "v1/nothing"));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public void WifiAddressesComeFirstThenTailscaleAndOtherTunnelsAreLeftOut()
    {
        int? R(string ip, bool gw = true, bool tunnel = false) => PhoneServer.Rank(IPAddress.Parse(ip), gw, tunnel);
        Assert.True(R("192.168.1.5") < R("100.101.102.103", gw: false, tunnel: true));
        Assert.True(R("10.0.0.7") < R("100.64.0.1"));
        Assert.Equal(10, R("100.127.255.254", gw: false, tunnel: true));
        Assert.Null(R("10.8.0.2", tunnel: true));    // a work VPN
        Assert.Null(R("169.254.3.4"));               // no DHCP
        Assert.Null(R("127.0.0.1"));
        Assert.Null(R("fe80::1"));
        Assert.False(PhoneServer.IsTailscale(IPAddress.Parse("100.128.0.1")));
        Assert.False(PhoneServer.IsTailscale(IPAddress.Parse("100.63.255.255")));
    }

    [Fact]
    public void TokensAreLongAndUrlSafe()
    {
        var a = PhoneServer.NewToken();
        Assert.True(a.Length >= 40);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
        Assert.NotEqual(a, PhoneServer.NewToken());
    }
}
