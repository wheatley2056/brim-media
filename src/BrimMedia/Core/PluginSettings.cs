using System.Globalization;
using System.Text.Json;

namespace BrimMedia.Core;

/// <summary>Einstellungen aus <c>plugin.json</c> mit den Werten des Nutzers. Zahlen und Schalter dürfen auch als Text kommen.</summary>
public sealed record PluginSettings
{
    public bool Lyrics { get; init; } = true;
    public string SpotifyClientId { get; init; } = "";
    public int QueueLength { get; init; } = 5;

    public static PluginSettings From(IReadOnlyDictionary<string, JsonElement> values)
    {
        var defaults = new PluginSettings();
        return new PluginSettings
        {
            Lyrics = Flag(values, "lyrics", defaults.Lyrics),
            SpotifyClientId = Text(values, "spotifyClientId").Trim(),
            QueueLength = (int)Number(values, "queueLength", defaults.QueueLength, 1, 10),
        };
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static double Number(IReadOnlyDictionary<string, JsonElement> values, string key, double fallback, double min, double max)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }
        double? number = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
        return number is { } n && double.IsFinite(n) ? Math.Clamp(n, min, max) : fallback;
    }

    private static bool Flag(IReadOnlyDictionary<string, JsonElement> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.Number => value.GetDouble() != 0,
            _ => fallback,
        };
    }
}
