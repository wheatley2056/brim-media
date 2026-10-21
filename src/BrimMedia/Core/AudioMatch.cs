namespace BrimMedia.Core;

/// <summary>
/// Welche Audiositzungen zum Player gehören. Die SMTC nennt eine AppUserModelId (<c>Spotify.exe</c>,
/// <c>SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify</c>, <c>Chrome</c>, <c>MSEdge</c>), Core Audio nur Prozesse.
/// Es passt, wenn der Prozessname in der Id vorkommt. Firefox meldet eine Kennung ohne Namen; dann gibt es keinen Treffer.
/// </summary>
public static class AudioMatch
{
    public static bool Matches(string sourceAppId, string processName)
    {
        if (processName.Length < 3 || string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }
        return sourceAppId.Contains(processName, StringComparison.OrdinalIgnoreCase);
    }
}
