using System.Text.Json;
using BrimMedia.Core;

namespace BrimMedia.Tests;

public class SmallTests
{
    [Theory]
    [InlineData("Spotify.exe", "Spotify", true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify", true)]
    [InlineData("Chrome", "chrome", true)]
    [InlineData("MSEdge", "msedge", true)]
    [InlineData("MSEdge", "msedgewebview2", false)]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", "AppleMusic", true)]
    [InlineData("308046B0AF4A39CB", "firefox", false)]
    [InlineData("Spotify.exe", "", false)]
    public void MatchesAudioSessionsToThePlayer(string sourceAppId, string process, bool expected)
    {
        Assert.Equal(expected, AudioMatch.Matches(sourceAppId, process));
    }

    [Fact]
    public void SettingsWithDefaultsAndBounds()
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"lyrics":"false","spotifyClientId":"  abc  "}""")!;

        Assert.Equal(new PluginSettings { Lyrics = false, SpotifyClientId = "abc" }, PluginSettings.From(values));
        Assert.Equal(new PluginSettings(), PluginSettings.From(new Dictionary<string, JsonElement>()));
    }

    [Fact]
    public void ManifestIsValidJsonWithMatchingWidgets()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BrimMedia", "plugin.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        var widgets = manifest.RootElement.GetProperty("widgets").EnumerateArray().Select(w => w.GetProperty("id").GetString()).ToList();

        Assert.Equal("de.brim.media", manifest.RootElement.GetProperty("id").GetString());
        Assert.Equal([Views.Lyrics, Views.Volume, Views.Queue], widgets);
    }
}
