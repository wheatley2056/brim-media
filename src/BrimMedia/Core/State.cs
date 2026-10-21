namespace BrimMedia.Core;

public enum LyricsStatus { Off, Idle, Loading, Found, NotFound, Failed }

public sealed record LyricsState(LyricsStatus Status, Lyrics Lyrics)
{
    public static readonly LyricsState Idle = new(LyricsStatus.Idle, Lyrics.None);
}

public enum QueueStatus
{
    /// <summary>Keine Client-ID eingetragen.</summary>
    Setup,
    /// <summary>Client-ID da, aber noch nicht angemeldet.</summary>
    Disconnected,
    /// <summary>Browser ist zur Anmeldung offen.</summary>
    Connecting,
    /// <summary>Angemeldet, aber gerade spielt ein anderer Player.</summary>
    NotSpotify,
    Loading,
    Ready,
    Failed,
}

public sealed record QueueState(QueueStatus Status, SpotifyQueue? Queue = null, string? Message = null);

/// <summary>Alles, was die Ansichten brauchen, zu einem Zeitpunkt.</summary>
public sealed record MediaState(
    Track? Track,
    TimeSpan Position,
    LyricsState Lyrics,
    AppVolume? Volume,
    QueueState Queue,
    int QueueLength);
