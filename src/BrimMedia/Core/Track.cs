namespace BrimMedia.Core;

/// <summary>Was gerade läuft, wie es die Windows-Medienschnittstelle meldet.</summary>
/// <param name="SourceAppId">AppUserModelId des Players, z. B. <c>Spotify.exe</c> oder <c>SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify</c>.</param>
/// <param name="PositionUpdated">Zeitpunkt, zu dem <paramref name="Position"/> galt; läuft die Wiedergabe, zählt die Zeit seitdem dazu.</param>
public sealed record Track(
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    string SourceAppId,
    string SourceName,
    bool IsPlaying,
    TimeSpan Position,
    DateTimeOffset PositionUpdated)
{
    /// <summary>Titel, Interpret und Album: gleich heißt derselbe Song, auch wenn die Position springt.</summary>
    public string Key => $"{Title}\u001F{Artist}\u001F{Album}";

    public bool IsSpotify => SourceAppId.Contains("spotify", StringComparison.OrdinalIgnoreCase);

    /// <summary>Position zum Zeitpunkt <paramref name="now"/>, hochgerechnet, solange gespielt wird.</summary>
    public TimeSpan PositionAt(DateTimeOffset now)
    {
        var position = Position;
        if (IsPlaying && PositionUpdated != default && now > PositionUpdated)
        {
            position += now - PositionUpdated;
        }
        if (Duration > TimeSpan.Zero && position > Duration)
        {
            position = Duration;
        }
        return position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }
}

/// <summary>Quelle für <see cref="Track"/>; unter Windows die SMTC.</summary>
public interface IPlayer
{
    /// <summary>Der aktuelle Titel, oder null, wenn nichts läuft.</summary>
    Track? Current { get; }

    /// <summary>Titel, Wiedergabe oder Position haben sich geändert.</summary>
    event Action? Changed;

    Task<bool> SeekAsync(TimeSpan position);
}

/// <summary>Lautstärke der App, die gerade spielt (nicht des ganzen Systems).</summary>
public sealed record AppVolume(string Name, float Volume, bool Muted);

/// <summary>Lautstärke pro App; unter Windows über Core Audio.</summary>
public interface IAppVolumes
{
    /// <summary>Die Audiositzung des Players, oder null, wenn er gerade keine hat.</summary>
    AppVolume? Read(string sourceAppId);

    void Set(string sourceAppId, float volume);

    void ToggleMute(string sourceAppId);
}
