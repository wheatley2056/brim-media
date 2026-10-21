using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrimMedia.Core;

namespace BrimMedia.Windows;

/// <summary>
/// Spotify-Anmeldung in <c>%LocalAppData%\Brim\PluginData\de.brim.media\spotify.bin</c>, mit DPAPI an das
/// Windows-Konto gebunden. Außerhalb des Plugin-Ordners, damit ein Update aus der Bibliothek sie nicht löscht.
/// </summary>
public sealed class DpapiTokenStore : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("de.brim.media/spotify");

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brim", "PluginData", "de.brim.media", "spotify.bin");

    public SpotifyTokens? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }
            var json = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize(json, SpotifyJson.Default.SpotifyTokens);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Spotify-Anmeldung nicht lesbar: {ex.Message}");
            return null;
        }
    }

    public void Save(SpotifyTokens? tokens)
    {
        try
        {
            if (tokens is null)
            {
                File.Delete(_path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(tokens, SpotifyJson.Default.SpotifyTokens);
            File.WriteAllBytes(_path, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Spotify-Anmeldung nicht gespeichert: {ex.Message}");
        }
    }
}
