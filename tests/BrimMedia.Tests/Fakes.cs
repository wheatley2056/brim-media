using System.Net;
using System.Text;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;
using BrimMedia.Core;

namespace BrimMedia.Tests;

internal sealed class RecordingSink : INotchSink
{
    public Dictionary<(string, TileSize), Widget> Widgets { get; } = [];
    public List<(string, TileSize)> WidgetSets { get; } = [];
    public Widget? View { get; private set; }
    public int ViewSets { get; private set; }
    public List<string> Huds { get; } = [];
    public List<string> Logs { get; } = [];

    public Task ShowHudAsync(HudParams hud)
    {
        Huds.Add(hud.Text ?? "");
        return Task.CompletedTask;
    }

    public Task SetViewAsync(string id, Widget content, int order)
    {
        Assert.Null(WidgetValidator.Validate(content));
        View = content;
        ViewSets++;
        return Task.CompletedTask;
    }

    public Task LogAsync(string message, string level)
    {
        Logs.Add($"{level}: {message}");
        return Task.CompletedTask;
    }

    public Task SetWidgetAsync(string id, TileSize size, Widget content)
    {
        Assert.Null(WidgetValidator.Validate(content));
        Widgets[(id, size)] = content;
        WidgetSets.Add((id, size));
        return Task.CompletedTask;
    }
}

internal sealed class FakePlayer : IPlayer
{
    public Track? Current { get; set; }
    public List<TimeSpan> Seeks { get; } = [];
    public event Action? Changed;

    public void Raise() => Changed?.Invoke();

    public Task<bool> SeekAsync(TimeSpan position)
    {
        Seeks.Add(position);
        return Task.FromResult(true);
    }
}

internal sealed class FakeVolumes : IAppVolumes
{
    public AppVolume? Volume { get; set; } = new("Spotify", 0.5f, false);
    public List<string> Calls { get; } = [];

    public AppVolume? Read(string sourceAppId) => Volume;
    public void Set(string sourceAppId, float volume) => Calls.Add($"set {sourceAppId} {volume:0.00}");
    public void ToggleMute(string sourceAppId) => Calls.Add($"mute {sourceAppId}");
}

internal sealed class FakeLyrics : ILyricsProvider
{
    public Lyrics Result { get; set; } = Lyrics.None;
    public Exception? Error { get; set; }
    public List<string> Requests { get; } = [];

    public Task<Lyrics> GetAsync(Track track, CancellationToken cancellationToken)
    {
        Requests.Add(track.Title);
        return Error is { } e ? Task.FromException<Lyrics>(e) : Task.FromResult(Result);
    }
}

internal sealed class MemoryTokenStore(SpotifyTokens? tokens = null) : ITokenStore
{
    public SpotifyTokens? Tokens { get; private set; } = tokens;
    public SpotifyTokens? Load() => Tokens;
    public void Save(SpotifyTokens? tokens) => Tokens = tokens;
}

/// <summary>Antwortet auf Anfragen nach Pfad; merkt sich, was ankam.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        return Respond(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal static class Tracks
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    public static Track Spotify(string title = "Bohemian Rhapsody", TimeSpan? position = null, bool playing = true) =>
        new(title, "Queen", "A Night at the Opera", TimeSpan.FromSeconds(354), "Spotify.exe", "Spotify", playing, position ?? TimeSpan.Zero, T0);

    public static Track Browser(string title = "Queen - Bohemian Rhapsody (Official Video)") =>
        new(title, "", "", TimeSpan.FromSeconds(360), "Chrome", "Chrome", true, TimeSpan.Zero, T0);
}
