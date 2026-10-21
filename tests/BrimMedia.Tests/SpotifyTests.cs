using System.Net;
using System.Net.Sockets;
using BrimMedia.Core;
using Microsoft.Extensions.Time.Testing;

namespace BrimMedia.Tests;

public class SpotifyTests
{
    private const string QueueJson = """
        {"currently_playing":{"id":"1","name":"Now","duration_ms":200000,"artists":[{"name":"A"},{"name":"B"}],
           "album":{"images":[{"url":"https://i.scdn.co/640","width":640},{"url":"https://i.scdn.co/64","width":64}]}},
         "queue":[{"id":"2","name":"Next","duration_ms":61000,"artists":[{"name":"C"}],"album":{"images":[]}},
                  {"id":"3","name":"Episode","duration_ms":3600000,"show":{"name":"Podcast"},"images":[{"url":"https://i.scdn.co/ep","width":300}]}]}
        """;

    [Fact]
    public void ChallengeFollowsRfc7636()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", SpotifyClient.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        Assert.Matches("^[A-Za-z0-9_-]{43,128}$", SpotifyClient.NewVerifier());
    }

    [Fact]
    public void AuthorizeUriHasClientRedirectAndScopes()
    {
        var client = new SpotifyClient(new HttpClient(new FakeHttp()), new MemoryTokenStore(), TimeProvider.System) { ClientId = "abc" };

        var uri = client.AuthorizeUri("verifier", "state1").AbsoluteUri;

        Assert.StartsWith("https://accounts.spotify.com/authorize?client_id=abc&response_type=code&redirect_uri=http%3A%2F%2F127.0.0.1%3A47823%2Fcallback", uri);
        Assert.Contains("scope=user-read-playback-state%20user-read-currently-playing", uri);
        Assert.Contains("state=state1", uri);
        Assert.Contains("code_challenge_method=S256", uri);
    }

    [Fact]
    public async Task ExchangesCodeAndStoresTokens()
    {
        var time = new FakeTimeProvider(Tracks.T0);
        var http = new FakeHttp { Respond = _ => FakeHttp.Json("""{"access_token":"at","refresh_token":"rt","expires_in":3600}""") };
        var store = new MemoryTokenStore();
        var client = new SpotifyClient(new HttpClient(http), store, time) { ClientId = "abc" };

        await client.ExchangeCodeAsync("code1", "verifier1", default);

        Assert.Equal(new SpotifyTokens("at", "rt", Tracks.T0.AddHours(1)), store.Tokens);
        Assert.True(client.IsConnected);
        Assert.Contains("grant_type=authorization_code", http.Bodies[0]);
        Assert.Contains("code_verifier=verifier1", http.Bodies[0]);
        Assert.Contains("client_id=abc", http.Bodies[0]);
    }

    [Fact]
    public async Task ReadsQueueAndRefreshesExpiredToken()
    {
        var time = new FakeTimeProvider(Tracks.T0);
        var http = new FakeHttp
        {
            Respond = r => r.RequestUri!.Host == "accounts.spotify.com"
                ? FakeHttp.Json("""{"access_token":"fresh","expires_in":3600}""")
                : FakeHttp.Json(QueueJson),
        };
        var store = new MemoryTokenStore(new SpotifyTokens("old", "rt", Tracks.T0.AddSeconds(30)));
        var client = new SpotifyClient(new HttpClient(http), store, time) { ClientId = "abc" };

        var queue = await client.GetQueueAsync(default);

        Assert.Contains("grant_type=refresh_token", http.Bodies[0]);
        Assert.Equal("Bearer fresh", http.Requests[1].Headers.Authorization!.ToString());
        Assert.Equal("rt", store.Tokens!.RefreshToken); // alter Refresh-Token bleibt
        Assert.Equal(new QueueItem("1", "Now", "A, B", TimeSpan.FromSeconds(200), "https://i.scdn.co/64"), queue!.Current);
        Assert.Equal(
            [
                new QueueItem("2", "Next", "C", TimeSpan.FromSeconds(61), null),
                new QueueItem("3", "Episode", "Podcast", TimeSpan.FromHours(1), "https://i.scdn.co/ep"),
            ],
            queue.Next);
    }

    [Fact]
    public async Task RevokedLoginDisconnects()
    {
        var http = new FakeHttp { Respond = _ => FakeHttp.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest) };
        var store = new MemoryTokenStore(new SpotifyTokens("old", "rt", Tracks.T0));
        var client = new SpotifyClient(new HttpClient(http), store, new FakeTimeProvider(Tracks.T0)) { ClientId = "abc" };

        await Assert.ThrowsAsync<SpotifyAuthException>(() => client.GetQueueAsync(default));

        Assert.Null(store.Tokens);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task NothingPlayingIsNull()
    {
        var http = new FakeHttp { Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent) };
        var store = new MemoryTokenStore(new SpotifyTokens("at", "rt", Tracks.T0.AddHours(1)));
        var client = new SpotifyClient(new HttpClient(http), store, new FakeTimeProvider(Tracks.T0)) { ClientId = "abc" };

        Assert.Null(await client.GetQueueAsync(default));
    }

    [Fact]
    public void ParsesCallback()
    {
        Assert.Equal(new LoopbackLogin.Result("a b", "s1", null), LoopbackLogin.Parse("/callback?code=a%20b&state=s1"));
        Assert.Equal(new LoopbackLogin.Result(null, "s1", "access_denied"), LoopbackLogin.Parse("/callback?error=access_denied&state=s1"));
    }

    [Fact]
    public async Task LoopbackReceivesTheBrowser()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wait = LoopbackLogin.WaitAsync(port, timeout.Token);
        using var browser = new HttpClient();
        var favicon = await browser.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
        var page = await browser.GetStringAsync($"http://127.0.0.1:{port}/callback?code=xyz&state=s1");
        var result = await wait;

        Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
        Assert.Contains("Mit Spotify verbunden", page);
        Assert.Equal("xyz", result.Code);
        Assert.Equal("s1", result.State);
    }
}
