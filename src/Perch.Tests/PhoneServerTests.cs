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

    private sealed class Fixture : IDisposable
    {
        public readonly List<(Guid Id, string Text)> Sent = new();
        public readonly PhoneServer Server;
        public readonly HttpClient Http = new();

        public Fixture()
        {
            Server = new PhoneServer(new InlineUi(), new PhoneServer.Host
            {
                Sessions = () => new[] { new PhoneSession(Tab, "fix login", "perch", "claude", "done", true, true, false) },
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
