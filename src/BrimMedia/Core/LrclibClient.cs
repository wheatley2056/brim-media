using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BrimMedia.Core;

/// <summary>
/// Songtexte von <see href="https://lrclib.net">LRCLIB</see>: frei, ohne Konto und Schlüssel, meist mit Zeitstempeln.
/// Erst die genaue Suche (Titel, Interpret, Album, Länge), dann die freie; Ergebnisse bleiben im Speicher.
/// </summary>
public sealed partial class LrclibClient(HttpClient http) : ILyricsProvider
{
    public static readonly Uri BaseAddress = new("https://lrclib.net/");

    private const int CacheSize = 64;
    private readonly Dictionary<string, Lyrics> _cache = [];
    private readonly Queue<string> _order = new();
    private readonly Lock _gate = new();

    public async Task<Lyrics> GetAsync(Track track, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(track.Key, out var cached))
            {
                return cached;
            }
        }
        var lyrics = await FetchAsync(track, cancellationToken);
        lock (_gate)
        {
            if (_cache.TryAdd(track.Key, lyrics))
            {
                _order.Enqueue(track.Key);
                while (_order.Count > CacheSize)
                {
                    _cache.Remove(_order.Dequeue());
                }
            }
        }
        return lyrics;
    }

    private async Task<Lyrics> FetchAsync(Track track, CancellationToken cancellationToken)
    {
        var (title, artist) = Clean(track.Title, track.Artist);
        if (title.Length == 0)
        {
            return Lyrics.None;
        }

        if (artist.Length > 0)
        {
            var query = Query(("track_name", title), ("artist_name", artist), ("album_name", track.Album),
                ("duration", track.Duration > TimeSpan.Zero ? ((int)Math.Round(track.Duration.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture) : ""));
            using var response = await http.GetAsync(new Uri(BaseAddress, "api/get?" + query), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var exact = await response.Content.ReadFromJsonAsync(LrclibJson.Default.LrclibRecord, cancellationToken);
                if (exact is not null && ToLyrics(exact) is { IsEmpty: false } found)
                {
                    return found;
                }
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }
        }

        var search = artist.Length > 0 ? Query(("track_name", title), ("artist_name", artist)) : Query(("q", title));
        using var searchResponse = await http.GetAsync(new Uri(BaseAddress, "api/search?" + search), cancellationToken);
        searchResponse.EnsureSuccessStatusCode();
        var results = await searchResponse.Content.ReadFromJsonAsync(LrclibJson.Default.LrclibRecordArray, cancellationToken) ?? [];
        var best = Pick(results, track.Duration);
        return best is null ? Lyrics.None : ToLyrics(best);
    }

    /// <summary>Am liebsten mit Zeitstempeln und passender Länge (±3 s), sonst irgendein Text.</summary>
    internal static LrclibRecord? Pick(IReadOnlyList<LrclibRecord> results, TimeSpan duration)
    {
        bool Fits(LrclibRecord r) => duration <= TimeSpan.Zero || r.Duration is not { } d || Math.Abs(d - duration.TotalSeconds) <= 3;
        return results.FirstOrDefault(r => Fits(r) && !string.IsNullOrWhiteSpace(r.SyncedLyrics))
            ?? results.FirstOrDefault(r => Fits(r) && (!string.IsNullOrWhiteSpace(r.PlainLyrics) || r.Instrumental))
            ?? results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics));
    }

    internal static Lyrics ToLyrics(LrclibRecord record)
    {
        var synced = Lyrics.ParseLrc(record.SyncedLyrics);
        if (synced.Count > 0)
        {
            return new Lyrics(synced, [], record.Instrumental);
        }
        var plain = Lyrics.FromPlain(record.PlainLyrics);
        return plain with { Instrumental = record.Instrumental };
    }

    /// <summary>
    /// Browser und YouTube melden oft „Interpret - Titel (Official Video)“ als Titel ohne Interpret.
    /// Zusätze, die in keiner Songtext-Datenbank stehen, fallen weg.
    /// </summary>
    internal static (string Title, string Artist) Clean(string title, string artist)
    {
        title = Noise().Replace(title, "").Trim();
        artist = artist.Trim();
        if (artist.EndsWith(" - Topic", StringComparison.Ordinal))
        {
            artist = artist[..^8].Trim();
        }
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (artist.Length == 0 && dash > 0)
        {
            artist = title[..dash].Trim();
            title = title[(dash + 3)..].Trim();
        }
        return (title, artist);
    }

    private static string Query(params (string Key, string Value)[] pairs) =>
        string.Join('&', pairs.Where(p => p.Value.Length > 0).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(official|video|lyrics?|audio|visuali[sz]er|hd|4k|mv)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();
}

public sealed record LrclibRecord
{
    public string? TrackName { get; init; }
    public string? ArtistName { get; init; }
    public double? Duration { get; init; }
    public bool Instrumental { get; init; }
    public string? PlainLyrics { get; init; }
    public string? SyncedLyrics { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LrclibRecord))]
[JsonSerializable(typeof(LrclibRecord[]))]
internal sealed partial class LrclibJson : JsonSerializerContext;
