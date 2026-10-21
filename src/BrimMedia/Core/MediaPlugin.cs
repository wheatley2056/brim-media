using System.Globalization;
using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace BrimMedia.Core;

/// <summary>
/// Hält Titel, Songtext, Lautstärke und Warteschlange zusammen und zeichnet die Widgets auf „Jetzt“.
/// Der Tab „Plugins“ ist nur zum Ein- und Ausschalten da; Media+ legt dort nichts ab.
/// Schickt nur, was sich geändert hat: Beim Songtext ist das eine Nachricht pro Zeile, nicht pro Takt.
/// </summary>
public sealed class MediaPlugin
{
    /// <summary>So oft wird geprüft, ob die nächste Songtext-Zeile dran ist.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

    /// <summary>Lautstärke wird höchstens so oft gelesen (sie kann sich im Windows-Mixer ändern).</summary>
    public static readonly TimeSpan VolumePoll = TimeSpan.FromSeconds(1);

    /// <summary>Warteschlange neu lesen, solange sie zu sehen ist.</summary>
    public static readonly TimeSpan QueuePoll = TimeSpan.FromSeconds(20);

    /// <summary>Nach einem Titelwechsel kurz warten: Spotify aktualisiert die Warteschlange etwas später.</summary>
    public static readonly TimeSpan QueueSettle = TimeSpan.FromSeconds(1.5);

    public static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Längeres ist ein Mitschnitt, Podcast oder Video, kein Song: dafür gar nicht erst suchen.</summary>
    public static readonly TimeSpan LongestSong = TimeSpan.FromMinutes(15);

    /// <summary>Wartezeiten, bevor nach einem Fehler (LRCLIB überlastet, offline) erneut gesucht wird.</summary>
    public static readonly TimeSpan[] LyricsRetries = [TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3)];

    public const string SetupGuide = "https://github.com/wheatley2056/brim-media#spotify-verbinden";

    private readonly INotchSink _sink;
    private readonly IPlayer _player;
    private readonly IAppVolumes _volumes;
    private readonly ILyricsProvider _lyricsProvider;
    private readonly SpotifyClient _spotify;
    private readonly IImageLoader? _images;
    private readonly TimeProvider _time;
    private readonly Func<Uri, Task> _openUrl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string, TileSize), string> _sentWidgets = [];

    private IReadOnlyDictionary<string, IReadOnlyList<TileSize>> _placed = new Dictionary<string, IReadOnlyList<TileSize>>();
    private string? _trackKey;
    private LyricsState _lyrics = LyricsState.Idle;
    private CancellationTokenSource? _lyricsFetch;
    private int _lyricsFailures;
    private DateTimeOffset? _lyricsRetry;
    private AppVolume? _volume;
    private DateTimeOffset _volumeRead;
    private QueueState _queue = new(QueueStatus.Setup);
    private DateTimeOffset _queueRead;
    private DateTimeOffset? _queueDue;
    private bool _queueLoading;
    private bool _connecting;

    public MediaPlugin(INotchSink sink, IPlayer player, IAppVolumes volumes, ILyricsProvider lyrics, SpotifyClient spotify,
        IImageLoader? images, TimeProvider time, Func<Uri, Task> openUrl, PluginSettings settings)
    {
        _sink = sink;
        _player = player;
        _volumes = volumes;
        _lyricsProvider = lyrics;
        _spotify = spotify;
        _images = images;
        _time = time;
        _openUrl = openUrl;
        Settings = settings;
        _spotify.ClientId = settings.SpotifyClientId;
        _player.Changed += () => _ = UpdateAsync();
        if (_images is not null)
        {
            _images.Loaded += () => _ = UpdateAsync();
        }
    }

    public PluginSettings Settings { get; private set; }

    private bool Shows(string widget) => _placed.ContainsKey(widget);

    /// <summary>Zeichnet im Takt von <see cref="Tick"/>, bis <paramref name="cancellationToken"/> abbricht.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await UpdateAsync();
            try
            {
                await Task.Delay(Tick, _time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Liest den Stand und zeichnet, was sich geändert hat.</summary>
    public async Task UpdateAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var now = _time.GetUtcNow();
            var track = _player.Current;
            if (track?.Key != _trackKey)
            {
                OnTrackChanged(track, now);
            }
            else if (track is not null && _lyricsRetry is { } retry && now >= retry)
            {
                _lyricsRetry = null;
                StartLyricsFetch(track);
            }

            if (track is not null && Shows(Views.Volume) && now - _volumeRead >= VolumePoll)
            {
                _volume = _volumes.Read(track.SourceAppId);
                _volumeRead = now;
            }

            _queue = QueueStatusFor(track);
            if (_queue.Status is QueueStatus.Ready or QueueStatus.Loading or QueueStatus.Failed && Shows(Views.Queue) && !_queueLoading
                && (_queueDue is { } due ? now >= due : now - _queueRead >= QueuePoll))
            {
                _queueDue = null;
                _queueLoading = true;
                _ = LoadQueueAsync();
            }

            await RenderLockedAsync(State(track, now));
        }
        finally
        {
            _gate.Release();
        }
    }

    private MediaState State(Track? track, DateTimeOffset now) =>
        new(track, track?.PositionAt(now) ?? TimeSpan.Zero, _lyrics, track is null ? null : _volume, _queue);

    private void OnTrackChanged(Track? track, DateTimeOffset now)
    {
        _trackKey = track?.Key;
        _lyricsFetch?.Cancel();
        _lyricsFetch = null;
        _volumeRead = default; // gleich lesen, der Player kann ein anderer sein
        _queueDue = now + QueueSettle;
        _lyricsFailures = 0;
        _lyricsRetry = null;
        if (track is null)
        {
            _lyrics = LyricsState.Idle;
            return;
        }
        if (!Settings.Lyrics)
        {
            _lyrics = new LyricsState(LyricsStatus.Off, Lyrics.None);
            return;
        }
        if (track.Duration > LongestSong)
        {
            _lyrics = new LyricsState(LyricsStatus.NotFound, Lyrics.None);
            return;
        }
        _lyrics = new LyricsState(LyricsStatus.Loading, Lyrics.None);
        StartLyricsFetch(track);
    }

    private void StartLyricsFetch(Track track)
    {
        _lyricsFetch?.Cancel();
        var fetch = _lyricsFetch = new CancellationTokenSource();
        _ = FetchLyricsAsync(track, fetch.Token);
    }

    private async Task FetchLyricsAsync(Track track, CancellationToken cancellationToken)
    {
        LyricsState result;
        try
        {
            var lyrics = await _lyricsProvider.GetAsync(track, cancellationToken);
            result = new LyricsState(lyrics.IsEmpty ? LyricsStatus.NotFound : LyricsStatus.Found, lyrics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // Nur ins Log: Das Widget zeigt dann einfach, was läuft, und es wird später erneut versucht.
            await LogAsync($"Songtext für „{track.Title}“ nicht geladen: {ex.Message}", "warn");
            result = new LyricsState(LyricsStatus.Failed, Lyrics.None);
        }
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (cancellationToken.IsCancellationRequested || _trackKey != track.Key)
            {
                return;
            }
            _lyrics = result;
            if (result.Status == LyricsStatus.Failed && _lyricsFailures < LyricsRetries.Length)
            {
                _lyricsRetry = _time.GetUtcNow() + LyricsRetries[_lyricsFailures++];
            }
        }
        finally
        {
            _gate.Release();
        }
        await UpdateAsync();
    }

    private QueueState QueueStatusFor(Track? track)
    {
        if (_connecting)
        {
            return new QueueState(QueueStatus.Connecting);
        }
        if (Settings.SpotifyClientId.Length == 0)
        {
            return new QueueState(QueueStatus.Setup);
        }
        if (!_spotify.IsConnected)
        {
            return new QueueState(QueueStatus.Disconnected);
        }
        if (track is not { IsSpotify: true })
        {
            return new QueueState(QueueStatus.NotSpotify);
        }
        return _queue.Status is QueueStatus.Ready or QueueStatus.Loading or QueueStatus.Failed
            ? _queue
            : new QueueState(QueueStatus.Loading, _queue.Queue);
    }

    private async Task LoadQueueAsync()
    {
        QueueState result;
        try
        {
            var queue = await _spotify.GetQueueAsync(CancellationToken.None);
            result = new QueueState(QueueStatus.Ready, queue ?? new SpotifyQueue(null, []));
        }
        catch (SpotifyAuthException ex)
        {
            await ShowHudAsync(Views.Icons.Warning, ex.Message);
            result = new QueueState(QueueStatus.Disconnected);
        }
        catch (Exception ex)
        {
            await LogAsync($"Spotify-Warteschlange nicht geladen: {ex.Message}", "warn");
            // Die letzte bekannte Warteschlange bleibt stehen; sonst zeigt das Widget einfach nichts.
            result = _queue.Queue is { } last ? new QueueState(QueueStatus.Ready, last) : new QueueState(QueueStatus.Failed);
        }
        await _gate.WaitAsync();
        try
        {
            _queue = result;
            _queueRead = _time.GetUtcNow();
            _queueLoading = false;
        }
        finally
        {
            _gate.Release();
        }
        await UpdateAsync();
    }

    // ───────── Von Brim ─────────

    public Task SetPlacedWidgetsAsync(IReadOnlyDictionary<string, IReadOnlyList<TileSize>> placed)
    {
        _placed = placed;
        return UpdateAsync();
    }

    public async Task ApplySettingsAsync(PluginSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            var lyricsChanged = settings.Lyrics != Settings.Lyrics;
            if (settings.SpotifyClientId != Settings.SpotifyClientId && _spotify.IsConnected)
            {
                // Die Anmeldung gehört zur alten App.
                _spotify.Disconnect();
            }
            Settings = settings;
            _spotify.ClientId = settings.SpotifyClientId;
            _queue = new QueueState(QueueStatus.Loading);
            _queueDue = _time.GetUtcNow();
            if (lyricsChanged)
            {
                _trackKey = null; // Titel gilt als neu: Songtext laden bzw. ausblenden
            }
        }
        finally
        {
            _gate.Release();
        }
        await UpdateAsync();
    }

    /// <summary>Knopf, Schieber oder Zeile in einem Widget, z. B. <c>seek:61230</c>.</summary>
    public async Task HandleActionAsync(string actionId, double? value)
    {
        var colon = actionId.IndexOf(':');
        var verb = colon < 0 ? actionId : actionId[..colon];
        var argument = colon < 0 ? "" : actionId[(colon + 1)..];
        var track = _player.Current;
        try
        {
            switch (verb)
            {
                case "seek" when long.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms):
                    await _player.SeekAsync(TimeSpan.FromMilliseconds(ms));
                    break;
                case "volume" when track is not null && value is { } v:
                    _volumes.Set(track.SourceAppId, (float)Math.Clamp(v, 0, 1));
                    _volumeRead = default;
                    break;
                case "mute" when track is not null:
                    _volumes.ToggleMute(track.SourceAppId);
                    _volumeRead = default;
                    break;
                case "spotify-connect":
                    await ConnectAsync();
                    break;
                default:
                    await LogAsync($"Unbekannte Aktion „{actionId}“", "warn");
                    break;
            }
        }
        catch (Exception ex)
        {
            await LogAsync($"Aktion {verb} fehlgeschlagen: {ex.Message}", "error");
        }
        await UpdateAsync();
    }

    /// <summary>Anmeldung im Browser; ohne Client-ID öffnet sich die Anleitung.</summary>
    public async Task ConnectAsync()
    {
        if (Settings.SpotifyClientId.Length == 0)
        {
            await ShowHudAsync(Glyphs.Settings, "Erst die Spotify-Client-ID in den Plugin-Einstellungen eintragen");
            await _openUrl(new Uri(SetupGuide));
            return;
        }
        if (_connecting)
        {
            return;
        }
        _connecting = true;
        try
        {
            await UpdateAsync();
            var verifier = SpotifyClient.NewVerifier();
            var state = SpotifyClient.NewVerifier()[..16];
            using var timeout = new CancellationTokenSource(LoginTimeout, _time);
            var wait = LoopbackLogin.WaitAsync(SpotifyClient.Port, timeout.Token);
            await _openUrl(_spotify.AuthorizeUri(verifier, state));
            var result = await wait;
            if (result.Code is null || result.State != state)
            {
                await ShowHudAsync(Views.Icons.Warning, result.Error == "access_denied" ? "Spotify-Anmeldung abgebrochen" : "Spotify-Anmeldung fehlgeschlagen");
                return;
            }
            await _spotify.ExchangeCodeAsync(result.Code, verifier, CancellationToken.None);
            _queueDue = _time.GetUtcNow();
            await ShowHudAsync(Glyphs.CheckMark, "Mit Spotify verbunden");
        }
        catch (OperationCanceledException)
        {
            await ShowHudAsync(Views.Icons.Warning, "Spotify-Anmeldung abgelaufen");
        }
        catch (System.Net.Sockets.SocketException)
        {
            await ShowHudAsync(Views.Icons.Warning, $"Port {SpotifyClient.Port} ist belegt, Anmeldung nicht möglich");
        }
        catch (SpotifyAuthException ex)
        {
            await ShowHudAsync(Views.Icons.Warning, ex.Message);
        }
        finally
        {
            _connecting = false;
        }
    }

    // ───────── An Brim, nur bei Änderungen ─────────

    private async Task RenderLockedAsync(MediaState state)
    {
        foreach (var (id, sizes) in _placed)
        {
            foreach (var size in sizes)
            {
                if (Views.Build(id, size, state, _images) is { } widget)
                {
                    await SetWidgetAsync(id, size, widget);
                }
            }
        }
    }

    private Task SetWidgetAsync(string id, TileSize size, Widget content)
    {
        var json = JsonSerializer.Serialize(content, PluginJson.Options);
        if (_sentWidgets.TryGetValue((id, size), out var sent) && sent == json)
        {
            return Task.CompletedTask;
        }
        _sentWidgets[(id, size)] = json;
        return SendAsync(() => _sink.SetWidgetAsync(id, size, content));
    }

    private Task ShowHudAsync(string glyph, string text) =>
        SendAsync(() => _sink.ShowHudAsync(new HudParams { Glyph = glyph, Text = text, Announcement = text }));

    private Task LogAsync(string message, string level) => SendAsync(() => _sink.LogAsync(message, level));

    private static async Task SendAsync(Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            // Brim hat abgelehnt (ungültige Parameter, zu viele Nachrichten) oder ist weg: im Plugin-Log festhalten.
            Console.Error.WriteLine($"Brim: {ex.Message}");
        }
    }
}
