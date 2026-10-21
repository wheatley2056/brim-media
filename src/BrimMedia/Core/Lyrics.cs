using System.Globalization;
using System.Text.RegularExpressions;

namespace BrimMedia.Core;

public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>Songtext: mit Zeitstempeln (<see cref="Synced"/>) oder nur als Text (<see cref="Plain"/>).</summary>
public sealed partial record Lyrics(IReadOnlyList<LyricLine> Synced, IReadOnlyList<string> Plain, bool Instrumental)
{
    public static readonly Lyrics None = new([], [], false);

    public bool IsSynced => Synced.Count > 0;

    public bool IsEmpty => !IsSynced && Plain.Count == 0 && !Instrumental;

    /// <summary>Index der Zeile, die bei <paramref name="position"/> läuft; −1 vor der ersten.</summary>
    public int IndexAt(TimeSpan position)
    {
        // Binäre Suche: letzte Zeile mit Time <= position.
        int lo = 0, hi = Synced.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Synced[mid].Time <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }

    public static Lyrics FromPlain(string? text) =>
        new([], SplitLines(text).ToList(), false);

    /// <summary>
    /// Liest LRC: <c>[mm:ss.xx]Text</c>, auch mehrere Zeitstempel vor einer Zeile und <c>[offset:+/-ms]</c>.
    /// Andere Kopfzeilen (<c>[ar:…]</c>, <c>[ti:…]</c>) und Wort-Zeitstempel (<c>&lt;mm:ss.xx&gt;</c>) fallen weg.
    /// </summary>
    public static IReadOnlyList<LyricLine> ParseLrc(string? lrc)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return lines;
        }
        var offset = TimeSpan.Zero;
        foreach (var raw in SplitLines(lrc, keepEmpty: true))
        {
            var line = raw.Trim();
            var offsetMatch = OffsetTag().Match(line);
            if (offsetMatch.Success)
            {
                // Positiver Offset heißt: der Text kommt früher.
                offset = -TimeSpan.FromMilliseconds(int.Parse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture));
                continue;
            }
            var times = new List<TimeSpan>();
            var rest = line;
            Match match;
            while ((match = TimeTag().Match(rest)).Success)
            {
                var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = match.Groups[3].Success ? match.Groups[3].Value : "";
                var ms = fraction.Length == 0 ? 0 : int.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
                times.Add(new TimeSpan(0, 0, minutes, seconds, ms));
                rest = rest[match.Length..];
            }
            if (times.Count == 0)
            {
                continue;
            }
            var text = WordTag().Replace(rest, "").Trim();
            foreach (var time in times)
            {
                var at = time + offset;
                lines.Add(new LyricLine(at < TimeSpan.Zero ? TimeSpan.Zero : at, text));
            }
        }
        // Stabil sortieren: gleiche Zeiten behalten ihre Reihenfolge.
        return lines.Select((l, i) => (l, i)).OrderBy(p => p.l.Time).ThenBy(p => p.i).Select(p => p.l).ToList();
    }

    private static IEnumerable<string> SplitLines(string? text, bool keepEmpty = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (keepEmpty || line.Trim().Length > 0)
            {
                yield return line.TrimEnd();
            }
        }
    }

    [GeneratedRegex(@"^\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordTag();
}

/// <summary>Woher Songtexte kommen.</summary>
public interface ILyricsProvider
{
    /// <summary>Der Text zu <paramref name="track"/>, <see cref="Lyrics.None"/>, wenn es keinen gibt. Wirft bei Netzwerkfehlern.</summary>
    Task<Lyrics> GetAsync(Track track, CancellationToken cancellationToken);
}
