using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;
using BrimMedia.Core;
using Microsoft.Extensions.Time.Testing;

namespace BrimMedia.Tests;

public class MediaPluginTests
{
    private static readonly Lyrics Song = new(Lyrics.ParseLrc("[00:01.00]First line\n[00:05.00]Second line\n[00:09.00]Third line"), [], false);

    private readonly RecordingSink _sink = new();
    private readonly FakePlayer _player = new();
    private readonly FakeVolumes _volumes = new();
    private readonly FakeLyrics _lyrics = new() { Result = Song };
    private readonly FakeTimeProvider _time = new(Tracks.T0);
    private readonly FakeHttp _http = new();
    private readonly MemoryTokenStore _tokens = new();
    private readonly List<Uri> _opened = [];

    private MediaPlugin Create(PluginSettings? settings = null) =>
        new(_sink, _player, _volumes, _lyrics, new SpotifyClient(new HttpClient(_http), _tokens, _time), null, _time,
            uri => { _opened.Add(uri); return Task.CompletedTask; }, settings ?? new PluginSettings());

    private static Dictionary<string, IReadOnlyList<TileSize>> Placed(params (string Id, TileSize Size)[] widgets) =>
        widgets.GroupBy(w => w.Id).ToDictionary(g => g.Key, g => (IReadOnlyList<TileSize>)g.Select(w => w.Size).ToList());

    private static string Texts(Widget? widget) => string.Join(" | ", Walk(widget).OfType<TextWidget>().Select(t => t.Text).Where(t => t.Trim().Length > 0));

    private static IEnumerable<Widget> Walk(Widget? widget)
    {
        if (widget is null)
        {
            yield break;
        }
        yield return widget;
        var children = widget switch
        {
            StackWidget s => s.Children,
            ListWidget l => l.Items,
            _ => [],
        };
        foreach (var child in children.SelectMany(Walk))
        {
            yield return child;
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task LyricsFollowThePlaybackAndOnlySendOnLineChanges()
    {
        var plugin = Create();
        _player.Current = Tracks.Spotify(position: TimeSpan.FromSeconds(2));
        await plugin.SetPlacedWidgetsAsync(Placed((Views.Lyrics, TileSize.Wide)));
        await Eventually(() => Texts(_sink.Widgets.GetValueOrDefault((Views.Lyrics, TileSize.Wide))).StartsWith("First line"));
        Assert.Equal("First line | Second line", Texts(_sink.Widgets[(Views.Lyrics, TileSize.Wide)]));
        var sets = _sink.WidgetSets.Count;

        _time.Advance(TimeSpan.FromSeconds(1));
        await plugin.UpdateAsync();
        Assert.Equal(sets, _sink.WidgetSets.Count); // gleiche Zeile: nichts geschickt

        _time.Advance(TimeSpan.FromSeconds(2.5));
        await plugin.UpdateAsync();
        Assert.Equal("Second line | Third line", Texts(_sink.Widgets[(Views.Lyrics, TileSize.Wide)]));
        Assert.Equal(["Bohemian Rhapsody"], _lyrics.Requests);
    }

    [Fact]
    public async Task NewTrackLoadsNewLyrics()
    {
        var plugin = Create();
        _player.Current = Tracks.Spotify();
        await plugin.SetPlacedWidgetsAsync(Placed((Views.Lyrics, TileSize.Large)));
        await Eventually(() => _lyrics.Requests.Count == 1);

        _lyrics.Result = Lyrics.None;
        _player.Current = Tracks.Spotify("Another One Bites the Dust");
        await plugin.UpdateAsync();

        await Eventually(() => Texts(_sink.Widgets[(Views.Lyrics, TileSize.Large)]) == "Songtext | Another One Bites the Dust | Queen");
        Assert.Equal(["Bohemian Rhapsody", "Another One Bites the Dust"], _lyrics.Requests);
    }

    [Fact]
    public async Task LyricsCanBeSwitchedOff()
    {
        var plugin = Create(new PluginSettings { Lyrics = false });
        _player.Current = Tracks.Spotify();
        await plugin.SetPlacedWidgetsAsync(Placed((Views.Lyrics, TileSize.Medium)));

        Assert.Equal("Bohemian Rhapsody | Queen", Texts(_sink.Widgets[(Views.Lyrics, TileSize.Medium)]));
        Assert.Empty(_lyrics.Requests);
    }

    [Fact]
    public async Task NetworkErrorShowsTheTrackAndRetriesLater()
    {
        _lyrics.Error = new HttpRequestException("offline");
        var plugin = Create();
        _player.Current = Tracks.Spotify(playing: false);
        await plugin.SetPlacedWidgetsAsync(Placed((Views.Lyrics, TileSize.Medium)));
        await Eventually(() => _sink.Logs.Any(l => l.Contains("offline")));
        await plugin.UpdateAsync();

        Assert.Equal("Bohemian Rhapsody | Queen", Texts(_sink.Widgets[(Views.Lyrics, TileSize.Medium)]));
        Assert.Empty(_sink.Huds);

        _lyrics.Error = null;
        await Eventually(() =>
        {
            // Der Fehler kommt im Hintergrund an; erst danach läuft die Wartezeit.
            _time.Advance(MediaPlugin.LyricsRetries[0]);
            plugin.UpdateAsync().GetAwaiter().GetResult();
            return Texts(_sink.Widgets[(Views.Lyrics, TileSize.Medium)]) == "♪ | First line";
        });
        Assert.Equal(2, _lyrics.Requests.Count);
    }

    [Fact]
    public async Task LongRecordingsAreNotSearched()
    {
        var plugin = Create();
        _player.Current = Tracks.Browser() with { Title = "Herr Bergmann spielt PORTAL", Artist = "Herr Bergmann", Duration = TimeSpan.FromHours(2) };
        await plugin.SetPlacedWidgetsAsync(Placed((Views.Lyrics, TileSize.Large)));

        Assert.Empty(_lyrics.Requests);
        Assert.Equal("Songtext | Herr Bergmann spielt PORTAL | Herr Bergmann", Texts(_sink.Widgets[(Views.Lyrics, TileSize.Large)]));
    }

    [Fact]
    public async Task ClickingALineSeeks()
    {
        var plugin = Create();
        _player.Current = Tracks.Spotify(position: TimeSpan.FromSeconds(6));
        await plugin.SetPlacedWidgetsAsync(Placed((Views.Lyrics, TileSize.Large)));
        await Eventually(() => Walk(_sink.Widgets[(Views.Lyrics, TileSize.Large)]).OfType<StackWidget>().Any(s => s.ActionId == "seek:9000"));

        await plugin.HandleActionAsync("seek:9000", null);

        Assert.Equal([TimeSpan.FromSeconds(9)], _player.Seeks);
    }

    [Fact]
    public async Task VolumeOfThePlayingApp()
    {
        var plugin = Create();
        _player.Current = Tracks.Spotify();
        await plugin.SetPlacedWidgetsAsync(Placed((Views.Volume, TileSize.Medium)));
        Assert.Equal("Spotify | 50 %", Texts(_sink.Widgets[(Views.Volume, TileSize.Medium)]));

        await plugin.HandleActionAsync("volume", 0.8);
        await plugin.HandleActionAsync("mute", null);
        Assert.Equal(["set Spotify.exe 0.80", "mute Spotify.exe"], _volumes.Calls);

        _volumes.Volume = new AppVolume("Spotify", 0.8f, true);
        _time.Advance(MediaPlugin.VolumePoll);
        await plugin.UpdateAsync();
        Assert.Equal("Spotify | stumm", Texts(_sink.Widgets[(Views.Volume, TileSize.Medium)]));
    }

    [Fact]
    public async Task VolumeWithoutAudioSession()
    {
        _volumes.Volume = null;
        var plugin = Create();
        _player.Current = Tracks.Browser();
        await plugin.SetPlacedWidgetsAsync(Placed((Views.Volume, TileSize.Small)));

        Assert.Equal("Chrome", Texts(_sink.Widgets[(Views.Volume, TileSize.Small)]));
    }

    [Fact]
    public async Task QueueNeedsAClientIdFirst()
    {
        var plugin = Create();
        _player.Current = Tracks.Browser();
        await plugin.SetPlacedWidgetsAsync(Placed((Views.Queue, TileSize.Large)));
        Assert.Equal("Als Nächstes | Nichts als Nächstes", Texts(_sink.Widgets[(Views.Queue, TileSize.Large)]));

        _player.Current = Tracks.Spotify();
        await plugin.UpdateAsync();
        Assert.Contains(Walk(_sink.Widgets[(Views.Queue, TileSize.Large)]).OfType<ButtonWidget>(), b => b.ActionId == "spotify-connect" && b.Label == "Einrichten");

        await plugin.HandleActionAsync("spotify-connect", null);

        Assert.Equal(MediaPlugin.SetupGuide, Assert.Single(_opened).AbsoluteUri);
        Assert.Contains(_sink.Huds, h => h.Contains("Client-ID"));
    }

    [Fact]
    public async Task QueueShowsWhatComesNextOnlyForSpotify()
    {
        _tokens.Save(new SpotifyTokens("at", "rt", Tracks.T0.AddHours(1)));
        _http.Respond = _ => FakeHttp.Json("""{"currently_playing":{"id":"1","name":"Now"},"queue":[{"id":"2","name":"Next up","duration_ms":61000,"artists":[{"name":"C"}]}]}""");
        var plugin = Create(new PluginSettings { SpotifyClientId = "abc" });
        _player.Current = Tracks.Spotify();
        await plugin.SetPlacedWidgetsAsync(Placed((Views.Queue, TileSize.Medium)));

        _time.Advance(MediaPlugin.QueueSettle);
        await plugin.UpdateAsync();
        await Eventually(() => Texts(_sink.Widgets[(Views.Queue, TileSize.Medium)]).Contains("Next up"));
        Assert.Equal("Als Nächstes | Next up | C | 1:01", Texts(_sink.Widgets[(Views.Queue, TileSize.Medium)]));

        _player.Current = Tracks.Browser();
        await plugin.UpdateAsync();
        Assert.Equal("Nichts als Nächstes", Texts(_sink.Widgets[(Views.Queue, TileSize.Medium)]));
    }

    [Fact]
    public async Task ChangingTheClientIdDisconnects()
    {
        _tokens.Save(new SpotifyTokens("at", "rt", Tracks.T0.AddHours(1)));
        var plugin = Create(new PluginSettings { SpotifyClientId = "abc" });

        await plugin.ApplySettingsAsync(new PluginSettings { SpotifyClientId = "other" });

        Assert.Null(_tokens.Tokens);
    }

    [Fact]
    public async Task EverySizeIsValid()
    {
        _tokens.Save(new SpotifyTokens("at", "rt", Tracks.T0.AddHours(1)));
        _http.Respond = _ => FakeHttp.Json("""{"queue":[{"id":"2","name":"Next","artists":[{"name":"C"}]},{"id":"3","name":"Then"}]}""");
        var plugin = Create(new PluginSettings { SpotifyClientId = "abc" });
        _player.Current = Tracks.Spotify(position: TimeSpan.FromSeconds(3));
        var all = Enum.GetValues<TileSize>().SelectMany(s => new[] { (Views.Lyrics, s), (Views.Volume, s), (Views.Queue, s) }).ToArray();

        await plugin.SetPlacedWidgetsAsync(Placed(all));
        _time.Advance(MediaPlugin.QueueSettle);
        await plugin.UpdateAsync();

        // RecordingSink prüft jede Nachricht mit Brims WidgetValidator.
        await Eventually(() => Texts(_sink.Widgets[(Views.Queue, TileSize.Full)]).Contains("Then"));
        Assert.Equal(all.Length, _sink.Widgets.Count);
        Assert.Contains("Second line", Texts(_sink.Widgets[(Views.Lyrics, TileSize.Full)]));
    }
}
