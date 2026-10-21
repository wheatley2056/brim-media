using System.Globalization;
using Brim.Core;
using Brim.Core.Widgets;

namespace BrimMedia.Core;

/// <summary>
/// Widgets für „Jetzt“. Media+ zeigt nur Widgets, nichts im Tab „Plugins“. Was gerade nicht geht
/// (kein Songtext, anderer Player, LRCLIB überlastet), fällt still weg: Statt einer Meldung zeigt das
/// Widget, was läuft. Reine Funktionen von <see cref="MediaState"/>, damit nur Änderungen verschickt werden.
/// </summary>
public static class Views
{
    public const string Lyrics = "lyrics", Queue = "queue", Volume = "volume";

    public static class Icons
    {
        public static readonly string Lyrics = char.ConvertFromUtf32(0xE8E4);
        public static readonly string Queue = char.ConvertFromUtf32(0xE8FD);
        public static readonly string Warning = char.ConvertFromUtf32(0xE7BA);
    }

    public const string SpotifyGreen = "#1DB954";
    public const string LyricsColor = "#FF8A66";

    /// <summary>Eine Zeile hat diesen Text, wenn sie leer ist (Zwischenspiel).</summary>
    public const string Interlude = "♪";

    public static Widget? Build(string id, TileSize size, MediaState state, IImageLoader? images = null) => id switch
    {
        Lyrics => LyricsWidget(size, state),
        Queue => QueueWidget(size, state, images),
        Volume => VolumeWidget(size, state),
        _ => null,
    };

    // ───────── Songtext ─────────

    public static Widget LyricsWidget(TileSize size, MediaState state)
    {
        var large = size is TileSize.Large or TileSize.Full;
        if (state.Track is not { } track)
        {
            return TileCard.Empty(Glyphs.MusicNote, "Gerade keine Musik");
        }
        var lyrics = state.Lyrics.Lyrics;
        if (state.Lyrics.Status != LyricsStatus.Found || lyrics.IsEmpty)
        {
            // Kein Text (noch nicht geladen, keiner gefunden, Quelle gerade weg): einfach zeigen, was läuft.
            return NowPlaying(track, size, lyrics.Instrumental ? "Instrumental" : null);
        }
        if (!lyrics.IsSynced)
        {
            var plain = new ListWidget(lyrics.Plain.Take(80).Select(l => (Widget)new TextWidget(Clip(l)) { Style = TextStyle.Secondary, FontSize = 12 }).ToList())
            {
                Spacing = 2,
                Scrollable = true,
                Grow = 1,
            };
            return large ? TileCard.Card(LyricsHeader(track), plain) : plain;
        }

        var index = lyrics.IndexAt(state.Position);
        var big = size is TileSize.Wide or TileSize.Full;
        if (!large)
        {
            return Column(
                CurrentLine(lyrics, index, big ? 16 : 14),
                Line(lyrics, index + 1, TextStyle.Secondary, 12));
        }
        // Groß: eine Zeile davor, zwei danach; ein Klick auf eine Zeile springt dorthin.
        return Column(
            [
                LyricsHeader(track),
                Seekable(lyrics, index - 1, Line(lyrics, index - 1, TextStyle.Caption, 12)),
                Seekable(lyrics, index, CurrentLine(lyrics, index, big ? 18 : 15)),
                Seekable(lyrics, index + 1, Line(lyrics, index + 1, TextStyle.Secondary, 13)),
                Seekable(lyrics, index + 2, Line(lyrics, index + 2, TextStyle.Secondary, 13)),
            ],
            Justify.SpaceBetween);
    }

    private static StackWidget LyricsHeader(Track track) =>
        TileCard.Header(Icons.Lyrics, LyricsColor, Clip(track.Title, 60));

    /// <summary>Titel und Interpret, wenn es keinen Songtext gibt.</summary>
    private static Widget NowPlaying(Track track, TileSize size, string? note)
    {
        var subtitle = string.Join(" · ", new[] { track.Artist, note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var lines = new StackWidget(
            new TextWidget(Clip(track.Title, 120)) { Style = TextStyle.Title, FontSize = size is TileSize.Small or TileSize.Medium ? 14 : 16 },
            new TextWidget(Clip(subtitle, 120)) { Style = TextStyle.Secondary, FontSize = 12 })
        {
            Orientation = StackOrientation.Vertical,
            Align = CrossAlign.Stretch,
            Spacing = 2,
            Grow = 1,
        };
        var row = new StackWidget(new IconWidget(Glyphs.MusicNote) { Size = 16, Color = LyricsColor }, lines) { Spacing = 10, Grow = 1 };
        return size is TileSize.Large or TileSize.Full
            ? TileCard.Card(TileCard.Header(Icons.Lyrics, LyricsColor, "Songtext"), row)
            : row;
    }

    private static Widget Seekable(Lyrics lyrics, int index, TextWidget line) =>
        index >= 0 && index < lyrics.Synced.Count
            ? new StackWidget(line with { Grow = 1 })
            {
                ActionId = "seek:" + ((long)lyrics.Synced[index].Time.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                AccessibleName = $"Zu {Duration(lyrics.Synced[index].Time)} springen",
            }
            : line;

    private static TextWidget CurrentLine(Lyrics lyrics, int index, double fontSize) =>
        new(index < 0 ? Interlude : Text(lyrics, index)) { Style = TextStyle.Title, FontSize = fontSize, Key = "current" };

    private static TextWidget Line(Lyrics lyrics, int index, TextStyle style, double fontSize) =>
        new(index >= 0 && index < lyrics.Synced.Count ? Text(lyrics, index) : " ") { Style = style, FontSize = fontSize };

    private static string Text(Lyrics lyrics, int index) =>
        lyrics.Synced[index].Text is { Length: > 0 } text ? Clip(text) : Interlude;

    // ───────── Lautstärke der Musik-App ─────────

    public static Widget VolumeWidget(TileSize size, MediaState state)
    {
        if (state.Track is null)
        {
            return TileCard.Empty(Glyphs.MusicNote, "Gerade keine Musik");
        }
        if (state.Volume is not { } volume)
        {
            return TileCard.Empty(Glyphs.Volume, Clip(state.Track.SourceName, 40));
        }
        var percent = Percent(volume);
        var mute = new ButtonWidget("mute")
        {
            Glyph = GlyphFor(volume),
            Size = 36,
            GlyphSize = 16,
            AccessibleName = volume.Muted ? $"{volume.Name} wieder einschalten" : $"{volume.Name} stumm schalten",
        };
        if (size == TileSize.Small)
        {
            return Column(
                new StackWidget(mute, new TextWidget(percent) { Style = TextStyle.Mono }) { Spacing = 2 },
                new TextWidget(Clip(volume.Name, 40)) { Style = TextStyle.Caption });
        }
        return Column(
            TileCard.Header(Glyphs.Volume, BrimColors.Slate, Clip(volume.Name, 60), percent),
            new StackWidget(
                mute,
                new ProgressWidget(volume.Muted ? 0 : volume.Volume)
                {
                    SeekActionId = "volume",
                    Live = false,
                    Grow = 1,
                    AccessibleName = $"Lautstärke {volume.Name}",
                })
            {
                Spacing = 6,
            });
    }

    public static string Percent(AppVolume volume) =>
        volume.Muted ? "stumm" : ((int)Math.Round(volume.Volume * 100)).ToString(CultureInfo.InvariantCulture) + " %";

    private static string GlyphFor(AppVolume volume)
    {
        var level = (int)Math.Round(volume.Volume * 100);
        return volume.Muted || level == 0 ? Glyphs.Mute : level switch
        {
            < 34 => Glyphs.Volume1,
            < 67 => Glyphs.Volume2,
            _ => Glyphs.Volume3,
        };
    }

    // ───────── Warteschlange (Spotify) ─────────

    public static Widget QueueWidget(TileSize size, MediaState state, IImageLoader? images = null)
    {
        var large = size is TileSize.Large or TileSize.Full;
        var header = TileCard.Header(Icons.Queue, SpotifyGreen, "Als Nächstes");
        var queue = state.Queue;
        Widget? empty = queue.Status switch
        {
            // Verbinden nur anbieten, wenn Spotify wirklich spielt; sonst gibt es nichts zu verbinden.
            QueueStatus.Setup or QueueStatus.Disconnected when state.Track is { IsSpotify: true } => Connect(queue.Status),
            QueueStatus.Connecting => TileCard.Empty(Glyphs.Link, "Anmeldung im Browser …"),
            _ when queue.Status != QueueStatus.Ready || queue.Queue is not { Next.Count: > 0 } => TileCard.Empty(Icons.Queue, "Nichts als Nächstes"),
            _ => null,
        };
        if (empty is not null)
        {
            return large ? TileCard.Card(header, empty) : empty;
        }
        var next = queue.Queue!.Next;
        if (!large)
        {
            return Column(
                new TextWidget("Als Nächstes") { Style = TextStyle.Caption },
                QueueRow(next[0], images, thumb: size == TileSize.Wide));
        }
        var rows = new List<Widget> { header };
        rows.AddRange(next.Take(3).Select(item => QueueRow(item, images, thumb: true)));
        return Column(rows, Justify.SpaceBetween);
    }

    private static Widget QueueRow(QueueItem item, IImageLoader? images, bool thumb)
    {
        var children = new List<Widget>();
        if (thumb)
        {
            children.Add(new ImageWidget(item.ImageUrl is { } url ? images?.TryGet(url) : null)
            {
                Width = 30,
                Height = 30,
                CornerRadius = 6,
                Placeholder = (SpotifyGreen, "#0B5C2A"),
            });
        }
        children.Add(new StackWidget(
            new TextWidget(Clip(item.Title, 120)) { FontSize = 13 },
            new TextWidget(Clip(item.Subtitle, 120)) { Style = TextStyle.Caption })
        {
            Orientation = StackOrientation.Vertical,
            Align = CrossAlign.Stretch,
            Grow = 1,
        });
        children.Add(new TextWidget(Duration(item.Duration)) { Style = TextStyle.MonoCaption });
        return new StackWidget(children) { Spacing = 8 };
    }

    private static Widget Connect(QueueStatus status) =>
        new StackWidget(
            new TextWidget("Warteschlange von Spotify zeigen") { Style = TextStyle.Secondary, FontSize = 12, Grow = 1 },
            new ButtonWidget("spotify-connect")
            {
                Kind = ButtonKind.Secondary,
                Glyph = Glyphs.Link,
                Label = status == QueueStatus.Setup ? "Einrichten" : "Verbinden",
            })
        {
            Spacing = 8,
            Grow = 1,
        };

    // ───────── Hilfen ─────────

    private static StackWidget Column(params IReadOnlyList<Widget> children) => Column(children, Justify.Center);

    private static StackWidget Column(IReadOnlyList<Widget> children, Justify justify) =>
        new(children)
        {
            Orientation = StackOrientation.Vertical,
            Align = CrossAlign.Stretch,
            Justify = justify,
            Spacing = 4,
            Grow = 1,
        };

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    public static string Clip(string text, int max = 300) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
