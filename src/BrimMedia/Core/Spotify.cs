using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace BrimMedia.Core;

/// <summary>Ein Titel oder eine Folge in der Spotify-Warteschlange.</summary>
public sealed record QueueItem(string Id, string Title, string Subtitle, TimeSpan Duration, string? ImageUrl);

public sealed record SpotifyQueue(QueueItem? Current, IReadOnlyList<QueueItem> Next);

/// <summary>Anmeldedaten für Spotify; unter Windows mit DPAPI verschlüsselt im Profil des Nutzers.</summary>
public sealed record SpotifyTokens(string AccessToken, string RefreshToken, DateTimeOffset Expires);

public interface ITokenStore
{
    SpotifyTokens? Load();
    void Save(SpotifyTokens? tokens);
}

/// <summary>Spotify meldet sich ab (Anmeldung widerrufen, Client-ID geändert): neu verbinden.</summary>
public sealed class SpotifyAuthException(string message) : Exception(message);

/// <summary>
/// Spotify-Web-API mit Authorization Code + PKCE: kein Client-Secret, nur die Client-ID einer eigenen App
/// auf developer.spotify.com. Der Browser kommt auf <see cref="RedirectUri"/> (Loopback) zurück.
/// Gelesen wird nur die Warteschlange (<c>user-read-playback-state</c>).
/// </summary>
public sealed class SpotifyClient(HttpClient http, ITokenStore store, TimeProvider time)
{
    public const int Port = 47823;
    public static readonly Uri RedirectUri = new($"http://127.0.0.1:{Port}/callback");
    public const string Scopes = "user-read-playback-state user-read-currently-playing";

    private static readonly Uri Accounts = new("https://accounts.spotify.com/");
    private static readonly Uri Api = new("https://api.spotify.com/v1/");

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private SpotifyTokens? _tokens = store.Load();

    public string ClientId { get; set; } = "";

    public bool IsConnected => _tokens is not null && ClientId.Length > 0;

    // ───────── Anmeldung ─────────

    /// <summary>Zufälliger Code-Verifier (43–128 Zeichen aus A–Z, a–z, 0–9, „-._~“).</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public Uri AuthorizeUri(string verifier, string state) =>
        new(Accounts, "authorize?" + Form(
            ("client_id", ClientId),
            ("response_type", "code"),
            ("redirect_uri", RedirectUri.AbsoluteUri),
            ("code_challenge_method", "S256"),
            ("code_challenge", Challenge(verifier)),
            ("scope", Scopes),
            ("state", state)));

    public async Task ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        var tokens = await TokenAsync(cancellationToken,
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", RedirectUri.AbsoluteUri),
            ("client_id", ClientId),
            ("code_verifier", verifier));
        Store(tokens);
    }

    public void Disconnect() => Store(null);

    private void Store(SpotifyTokens? tokens)
    {
        _tokens = tokens;
        store.Save(tokens);
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            var tokens = _tokens ?? throw new SpotifyAuthException("Nicht mit Spotify verbunden");
            if (tokens.Expires - time.GetUtcNow() > TimeSpan.FromMinutes(1))
            {
                return tokens.AccessToken;
            }
            var fresh = await TokenAsync(cancellationToken,
                ("grant_type", "refresh_token"),
                ("refresh_token", tokens.RefreshToken),
                ("client_id", ClientId));
            Store(fresh);
            return fresh.AccessToken;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private async Task<SpotifyTokens> TokenAsync(CancellationToken cancellationToken, params (string, string)[] form)
    {
        using var content = new StringContent(Form(form), Encoding.ASCII, "application/x-www-form-urlencoded");
        using var response = await http.PostAsync(new Uri(Accounts, "api/token"), content, cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            // invalid_grant: Anmeldung widerrufen oder abgelaufen; invalid_client: Client-ID falsch.
            Store(null);
            throw new SpotifyAuthException("Spotify hat die Anmeldung abgelehnt. Bitte neu verbinden.");
        }
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync(SpotifyJson.Default.TokenResponse, cancellationToken)
            ?? throw new HttpRequestException("Leere Antwort von Spotify");
        return new SpotifyTokens(
            token.AccessToken,
            // Beim Erneuern schickt Spotify nicht immer einen neuen Refresh-Token mit.
            token.RefreshToken ?? _tokens?.RefreshToken ?? "",
            time.GetUtcNow() + TimeSpan.FromSeconds(token.ExpiresIn));
    }

    // ───────── Warteschlange ─────────

    /// <summary>Was gerade läuft und was danach kommt; null, wenn auf keinem Gerät etwas läuft.</summary>
    public async Task<SpotifyQueue?> GetQueueAsync(CancellationToken cancellationToken)
    {
        var token = await AccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Api, "me/player/queue"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            Store(null);
            throw new SpotifyAuthException("Spotify-Anmeldung abgelaufen. Bitte neu verbinden.");
        }
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        var queue = await response.Content.ReadFromJsonAsync(SpotifyJson.Default.QueueResponse, cancellationToken);
        if (queue is null)
        {
            return null;
        }
        return new SpotifyQueue(ToItem(queue.CurrentlyPlaying), (queue.Queue ?? []).Select(ToItem).OfType<QueueItem>().ToList());
    }

    internal static QueueItem? ToItem(SpotifyItem? item)
    {
        if (item?.Name is not { Length: > 0 } name)
        {
            return null;
        }
        var subtitle = item.Artists is { Count: > 0 } artists
            ? string.Join(", ", artists.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)))
            : item.Show?.Name ?? "";
        var images = item.Album?.Images ?? item.Images ?? [];
        // Das kleinste Bild genügt für ein Vorschaubild (Spotify liefert 640, 300 und 64 Pixel).
        var image = images.Where(i => i.Url is not null).OrderBy(i => i.Width ?? int.MaxValue).FirstOrDefault()?.Url;
        return new QueueItem(item.Id ?? name, name, subtitle, TimeSpan.FromMilliseconds(item.DurationMs), image);
    }

    private static string Form(params (string Key, string Value)[] pairs) =>
        string.Join('&', pairs.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record TokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
}

public sealed record QueueResponse
{
    [JsonPropertyName("currently_playing")] public SpotifyItem? CurrentlyPlaying { get; init; }
    [JsonPropertyName("queue")] public List<SpotifyItem>? Queue { get; init; }
}

public sealed record SpotifyItem
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("duration_ms")] public long DurationMs { get; init; }
    [JsonPropertyName("artists")] public List<SpotifyNamed>? Artists { get; init; }
    [JsonPropertyName("album")] public SpotifyAlbum? Album { get; init; }
    [JsonPropertyName("show")] public SpotifyNamed? Show { get; init; }
    [JsonPropertyName("images")] public List<SpotifyImage>? Images { get; init; }
}

public sealed record SpotifyNamed
{
    [JsonPropertyName("name")] public string? Name { get; init; }
}

public sealed record SpotifyAlbum
{
    [JsonPropertyName("images")] public List<SpotifyImage>? Images { get; init; }
}

public sealed record SpotifyImage
{
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("width")] public int? Width { get; init; }
}

[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(QueueResponse))]
[JsonSerializable(typeof(SpotifyTokens))]
internal sealed partial class SpotifyJson : JsonSerializerContext;
