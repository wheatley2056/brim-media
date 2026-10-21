using System.Security.Cryptography;
using Brim.Core.Widgets;

namespace BrimMedia.Core;

/// <summary>Vorschaubilder der Warteschlange.</summary>
public interface IImageLoader
{
    /// <summary>Das Bild, wenn es schon geladen ist; sonst null und es wird im Hintergrund geladen.</summary>
    ImageData? TryGet(string url);

    /// <summary>Ein Bild ist fertig geladen.</summary>
    event Action? Loaded;
}

/// <summary>Lädt Bilder von Spotifys CDN (nur https, höchstens 256 KB) und hält die letzten im Speicher.</summary>
public sealed class HttpImageLoader(HttpClient http) : IImageLoader
{
    private const int CacheSize = 40;
    private const int MaxBytes = 256 * 1024;
    private readonly Dictionary<string, ImageData?> _cache = [];
    private readonly Queue<string> _order = new();
    private readonly Lock _gate = new();

    public event Action? Loaded;

    public ImageData? TryGet(string url)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(url, out var image))
            {
                return image;
            }
            _cache[url] = null; // lädt
            _order.Enqueue(url);
            while (_order.Count > CacheSize)
            {
                _cache.Remove(_order.Dequeue());
            }
        }
        _ = LoadAsync(url);
        return null;
    }

    private async Task LoadAsync(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return;
            }
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                return;
            }
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length > MaxBytes)
            {
                return;
            }
            var image = new ImageData(Convert.ToHexString(SHA1.HashData(bytes)), bytes);
            lock (_gate)
            {
                if (_cache.ContainsKey(url))
                {
                    _cache[url] = image;
                }
            }
            Loaded?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Bild {url}: {ex.Message}");
        }
    }
}
