using BrimMedia.Core;
using Windows.ApplicationModel;
using Windows.Media.Control;

namespace BrimMedia.Windows;

/// <summary>
/// Der aktuelle Titel über die Windows-Medienschnittstelle (SMTC), wie Brims eigenes Medienmodul.
/// Spotify, Browser, Apple Music, VLC … melden sich hier; ein Konto braucht es nicht.
/// </summary>
public sealed class SmtcPlayer : IPlayer, IDisposable
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _sourceName = "";

    public Track? Current { get; private set; }

    public event Action? Changed;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (sender, _) => Attach(sender.GetCurrentSession());
        Attach(_manager.GetCurrentSession());
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        _session = session;
        if (session is null)
        {
            Current = null;
            Changed?.Invoke();
            return;
        }
        _sourceName = ResolveSourceName(session.SourceAppUserModelId) ?? "";
        session.MediaPropertiesChanged += OnMediaChanged;
        session.PlaybackInfoChanged += OnPlaybackChanged;
        session.TimelinePropertiesChanged += OnTimelineChanged;
        _ = RefreshAsync();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = RefreshAsync();

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = RefreshAsync();

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }
        await _refresh.WaitAsync();
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            if (session != _session)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(props?.Title))
            {
                Current = null;
            }
            else
            {
                var artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
                Current = new Track(
                    props.Title,
                    artist,
                    props.AlbumTitle ?? "",
                    timeline is null ? TimeSpan.Zero : timeline.EndTime - timeline.StartTime,
                    session.SourceAppUserModelId ?? "",
                    _sourceName,
                    playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    timeline?.Position ?? TimeSpan.Zero,
                    timeline?.LastUpdatedTime ?? DateTimeOffset.UtcNow);
            }
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // Manche Player werfen beim Wechsel der Sitzung; dann beim nächsten Ereignis erneut versuchen.
            Console.Error.WriteLine($"SMTC: {ex.Message}");
        }
        finally
        {
            _refresh.Release();
        }
    }

    public async Task<bool> SeekAsync(TimeSpan position)
    {
        if (_session is not { } session)
        {
            return false;
        }
        var ok = await session.TryChangePlaybackPositionAsync(position.Ticks);
        await RefreshAsync();
        return ok;
    }

    /// <summary>Wie in Brims Medienmodul: Anzeigename verpackter Apps, sonst aus der AppUserModelId.</summary>
    private static string? ResolveSourceName(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return null;
        }
        try
        {
            var info = AppInfo.GetFromAppUserModelId(appUserModelId);
            if (!string.IsNullOrWhiteSpace(info?.DisplayInfo?.DisplayName))
            {
                return info.DisplayInfo.DisplayName;
            }
        }
        catch (Exception)
        {
            // Unverpackte Apps (Spotify.exe, chrome) haben keine AppInfo.
        }
        var name = appUserModelId;
        var bang = name.IndexOf('!');
        if (bang >= 0)
        {
            name = name[(bang + 1)..];
        }
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        name = name.Split('.', '_')[^1];
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : null;
    }

    public void Dispose() => Attach(null);
}
