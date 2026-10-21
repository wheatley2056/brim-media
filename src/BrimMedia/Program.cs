// Media+ für Brim: ergänzt Brims eigene Wiedergabe um mitlaufende Songtexte, die Lautstärke der Musik-App
// und, nach optionaler Anmeldung bei Spotify, die Warteschlange. Widgets auf „Jetzt“ und eine Karte im Tab „Plugins“.
using System.Diagnostics;
using Brim.Sdk;
using BrimMedia.Core;
using BrimMedia.Windows;

if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
{
    Console.Error.WriteLine("Media+ läuft nur unter Windows 10 (2004) oder neuer.");
    return 1;
}

await using var brim = await BrimPlugin.ConnectAsync();

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
http.DefaultRequestHeaders.UserAgent.ParseAdd($"BrimMedia/{typeof(MediaPlugin).Assembly.GetName().Version?.ToString(3)} (+https://github.com/wheatley2056/brim-media)");

using var player = new SmtcPlayer();
using var volumes = new CoreAudioVolumes();
var settings = PluginSettings.From(brim.Settings);
var plugin = new MediaPlugin(
    new BrimSink(brim),
    player,
    volumes,
    new LrclibClient(http),
    new SpotifyClient(http, new DpapiTokenStore(), TimeProvider.System),
    new HttpImageLoader(http),
    TimeProvider.System,
    OpenUrlAsync,
    settings);

brim.ActionInvoked += (action, value) => _ = plugin.HandleActionAsync(action, value);
brim.SettingsChanged += values => _ = plugin.ApplySettingsAsync(PluginSettings.From(values));
brim.TabVisibilityChanged += visible => _ = plugin.SetTabVisibleAsync(visible);
brim.WidgetsPlaced += placed => _ = plugin.SetPlacedWidgetsAsync(placed);
_ = plugin.SetPlacedWidgetsAsync(brim.PlacedWidgets);

await player.StartAsync();

using var stop = new CancellationTokenSource();
var loop = plugin.RunAsync(stop.Token);
await brim.RunAsync();

// Brim gibt 2 Sekunden zum Beenden.
stop.Cancel();
await Task.WhenAny(loop, Task.Delay(500));
return 0;

static Task OpenUrlAsync(Uri link)
{
    Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    return Task.CompletedTask;
}
