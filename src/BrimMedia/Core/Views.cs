using System.Globalization;
using Brim.Core;
using Brim.Core.Widgets;

namespace BrimMedia.Core;

/// <summary>
/// Widgets für „Jetzt“ und die Karte im Tab „Plugins“. Reine Funktionen von <see cref="MediaState"/>,
/// damit nur verschickt wird, was sich geändert hat, und Tests die Ausgabe direkt prüfen können.
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
        if (LyricsEmpty(state) is { } empty)
        {
            return size is TileSize.Large or TileSize.Full
                ? TileCard.Card(LyricsHeader(state), empty)
                : empty;
        }
        var lyrics = state.Lyrics.Lyrics;
        if (!lyrics.IsSynced)
        {
            var plain = new ListWidget(lyrics.Plain.Take(80).Select(l => (Widget)new TextWidget(Clip(l)) { Style = TextStyle.Secondary, FontSize = 12 }).ToList())
            {
                Spacing = 2,
                Scrollable = true,
                Grow = 1,
            };
            return size is TileSize.Large or TileSize.Full
                ? TileCard.Card(LyricsHeader(state, "ohne Zeitstempel"), plain)
                : plain;
        }

        var index = lyrics.IndexAt(state.Position);
        var big = size is TileSize.Wide or TileSize.Full;
        if (size is TileSize.Small or TileSize.Medium or TileSize.Wide)
        {
            return Column(
                CurrentLine(lyrics, index, big ? 16 : 14),
                Line(lyrics, index + 1, TextStyle.Secondary, 12));
        }
        var rows = new List<Widget> { LyricsHeader(state) };
        rows.Add(Line(lyrics, index - 1, TextStyle.Caption, 12));
        rows.Add(CurrentLine(lyrics, index, big ? 18 : 15));
        rows.Add(Line(lyrics, index + 1, TextStyle.Secondary, 13));
        rows.Add(Line(lyrics, index + 2, TextStyle.Secondary, 13));
        return Column(rows, Justify.SpaceBetween);
    }

    private static StackWidget LyricsHeader(MediaState state, string? trailing = null) =>
        TileCard.Header(Icons.Lyrics, LyricsColor, state.Track is { } t ? Clip(t.Title, 60) : "Songtext", trailing);

    /// <summary>Kein Text zum Zeigen: warum nicht.</summary>
    private static Widget? LyricsEmpty(MediaState state)
    {
        if (state.Track is null)
        {
            return TileCard.Empty(Glyphs.MusicNote, "Gerade keine Musik");
        }
        return state.Lyrics.Status switch
        {
            LyricsStatus.Off => TileCard.Empty(Icons.Lyrics, "Songtexte sind ausgeschaltet"),
            LyricsStatus.Idle or LyricsStatus.Loading => TileCard.Empty(Icons.Lyrics, "Songtext wird gesucht …"),
            LyricsStatus.NotFound => TileCard.Empty(Icons.Lyrics, "Kein Songtext gefunden"),
            LyricsStatus.Failed => TileCard.Empty(Icons.Warning, "Songtexte gerade nicht erreichbar"),
            _ when state.Lyrics.Lyrics.Instrumental && !state.Lyrics.Lyrics.IsSynced && state.Lyrics.Lyrics.Plain.Count == 0
                => TileCard.Empty(Glyphs.MusicNote, "Instrumental"),
            _ when state.Lyrics.Lyrics.IsEmpty => TileCard.Empty(Icons.Lyrics, "Kein Songtext gefunden"),
            _ => null,
        };
    }

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
            return TileCard.Empty(Glyphs.Volume, $"{Clip(state.Track.SourceName, 40)} gibt gerade keinen Ton aus");
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
        var header = TileCard.Header(Icons.Queue, SpotifyGreen, "Als Nächstes");
        var queue = state.Queue;
        Widget? empty = queue.Status switch
        {
            QueueStatus.Setup => Hint("Spotify verbinden für die Warteschlange", "spotify-connect", "Einrichten"),
            QueueStatus.Disconnected => Hint("Mit Spotify verbinden", "spotify-connect", "Verbinden"),
            QueueStatus.Connecting => TileCard.Empty(Glyphs.Link, "Anmeldung im Browser …"),
            QueueStatus.NotSpotify => TileCard.Empty(Icons.Queue, "Warteschlange gibt es nur für Spotify"),
            QueueStatus.Loading when queue.Queue is null => TileCard.Empty(Icons.Queue, "Wird geladen …"),
            QueueStatus.Failed => TileCard.Empty(Icons.Warning, Clip(queue.Message ?? "Spotify gerade nicht erreichbar", 80)),
            _ when queue.Queue is not { Next.Count: > 0 } => TileCard.Empty(Icons.Queue, "Nichts in der Warteschlange"),
            _ => null,
        };
        if (empty is not null)
        {
            return size is TileSize.Large or TileSize.Full ? TileCard.Card(header, empty) : empty;
        }
        var next = queue.Queue!.Next;
        if (size is TileSize.Small or TileSize.Medium or TileSize.Wide)
        {
            return Column(
                new TextWidget("Als Nächstes") { Style = TextStyle.Caption },
                QueueRow(next[0], images, thumb: size == TileSize.Wide));
        }
        var rows = new List<Widget> { header };
        rows.AddRange(next.Take(Math.Min(3, state.QueueLength)).Select(item => QueueRow(item, images, thumb: true)));
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

    private static Widget Hint(string text, string actionId, string label) =>
        new StackWidget(
            new TextWidget(text) { Style = TextStyle.Secondary, FontSize = 12, Grow = 1 },
            new ButtonWidget(actionId) { Kind = ButtonKind.Secondary, Glyph = Glyphs.Link, Label = label })
        {
            Spacing = 8,
            Grow = 1,
        };

    // ───────── Karte im Tab „Plugins“ ─────────

    public static Widget Card(MediaState state, IImageLoader? images)
    {
        var children = new List<Widget>();
        var title = state.Track is { } track ? $"{Clip(track.Title, 80)} · {Clip(track.Artist, 60)}" : "Media+";
        children.Add(new StackWidget(
            new IconWidget(Glyphs.MusicNote) { Size = 14, Color = LyricsColor },
            new TextWidget(title) { Style = TextStyle.Secondary, FontSize = 12, Grow = 1 },
            new TextWidget(state.Track?.SourceName ?? "") { Style = TextStyle.Caption })
        {
            Spacing = 8,
        });

        children.Add(CardLyrics(state));

        if (state.Track is not null)
        {
            children.Add(VolumeWidget(TileSize.Wide, state));
        }

        children.Add(TileCard.Header(Icons.Queue, SpotifyGreen, "Als Nächstes",
            state.Queue.Status is QueueStatus.Ready or QueueStatus.Loading or QueueStatus.NotSpotify ? "Spotify" : null));
        if (state.Queue is { Status: QueueStatus.Ready or QueueStatus.Loading, Queue.Next.Count: > 0 } ready)
        {
            children.AddRange(ready.Queue!.Next.Take(state.QueueLength).Select(item => QueueRow(item, images, thumb: true)));
            children.Add(new StackWidget(
                new SpacerWidget { Grow = 1 },
                new ButtonWidget("spotify-disconnect") { Kind = ButtonKind.Subtle, Label = "Spotify trennen", GlyphSize = 12 }));
        }
        else
        {
            children.Add(QueueWidget(TileSize.Wide, state, images));
        }

        return new StackWidget(children)
        {
            Orientation = StackOrientation.Vertical,
            Align = CrossAlign.Stretch,
            Spacing = 10,
            Background = BrimColors.Graphite,
            CornerRadius = 14,
            Padding = (12, 10, 12, 12),
        };
    }

    /// <summary>Im Tab ist Platz: zwei Zeilen davor, sechs danach; ein Klick springt zur Zeile.</summary>
    private static Widget CardLyrics(MediaState state)
    {
        if (LyricsEmpty(state) is { } empty)
        {
            return empty;
        }
        var lyrics = state.Lyrics.Lyrics;
        if (!lyrics.IsSynced)
        {
            return new ListWidget(lyrics.Plain.Take(150).Select(l => (Widget)new TextWidget(Clip(l)) { Style = TextStyle.Body }).ToList())
            {
                Spacing = 3,
                Scrollable = true,
            };
        }
        var index = lyrics.IndexAt(state.Position);
        var first = Math.Max(0, index - 2);
        var items = new List<Widget>();
        for (var i = first; i < Math.Min(lyrics.Synced.Count, first + 9); i++)
        {
            var line = lyrics.Synced[i];
            var current = i == index;
            items.Add(new StackWidget(new TextWidget(Text(lyrics, i))
            {
                Style = current ? TextStyle.Title : TextStyle.Secondary,
                FontSize = current ? 17 : 14,
                Color = current ? null : i < index ? "#6A6E78" : null,
                Wrap = current,
                Grow = 1,
            })
            {
                ActionId = "seek:" + ((long)line.Time.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                AccessibleName = $"Zu {Duration(line.Time)} springen: {line.Text}",
                Padding = (0, 2, 0, 2),
            });
        }
        return new ListWidget(items) { Spacing = 2 };
    }

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
