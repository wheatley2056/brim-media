using BrimMedia.Core;

namespace BrimMedia.Tests;

public class LyricsTests
{
    [Fact]
    public void ParsesTimestampsAndIgnoresHeaders()
    {
        var lines = Lyrics.ParseLrc("[ar:Queen]\n[ti:Bohemian Rhapsody]\n[00:00.50]Is this the real life?\n[00:04.2]Is this just fantasy?\n[01:02.345]Mama\n");

        Assert.Equal(
            [
                new LyricLine(TimeSpan.FromMilliseconds(500), "Is this the real life?"),
                new LyricLine(TimeSpan.FromMilliseconds(4200), "Is this just fantasy?"),
                new LyricLine(TimeSpan.FromMilliseconds(62345), "Mama"),
            ],
            lines);
    }

    [Fact]
    public void RepeatedLinesWithSeveralTimestampsAreSorted()
    {
        var lines = Lyrics.ParseLrc("[00:10.00][00:30.00]Chorus\n[00:20.00]Verse\n[00:40.00]");

        Assert.Equal(["Chorus", "Verse", "Chorus", ""], lines.Select(l => l.Text));
        Assert.Equal([10, 20, 30, 40], lines.Select(l => (int)l.Time.TotalSeconds));
    }

    [Fact]
    public void OffsetMovesLinesEarlierAndWordTagsDisappear()
    {
        var lines = Lyrics.ParseLrc("[offset:+500]\n[00:01.00]<00:01.00>Hello <00:01.50>world\r\n");

        Assert.Equal(new LyricLine(TimeSpan.FromMilliseconds(500), "Hello world"), Assert.Single(lines));
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(999, -1)]
    [InlineData(1000, 0)]
    [InlineData(2500, 1)]
    [InlineData(99000, 2)]
    public void IndexAtFindsTheRunningLine(int ms, int expected)
    {
        var lyrics = new Lyrics(Lyrics.ParseLrc("[00:01.00]a\n[00:02.00]b\n[00:03.00]c"), [], false);

        Assert.Equal(expected, lyrics.IndexAt(TimeSpan.FromMilliseconds(ms)));
    }

    [Fact]
    public void PlainTextDropsEmptyLines()
    {
        var lyrics = Lyrics.FromPlain("one\r\n\r\ntwo\n  \nthree");

        Assert.Equal(["one", "two", "three"], lyrics.Plain);
        Assert.False(lyrics.IsSynced);
        Assert.False(lyrics.IsEmpty);
    }

    [Fact]
    public void PositionRunsOnWhilePlayingAndStopsAtTheEnd()
    {
        var track = Tracks.Spotify(position: TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(15), track.PositionAt(Tracks.T0.AddSeconds(5)));
        Assert.Equal(track.Duration, track.PositionAt(Tracks.T0.AddHours(1)));
        Assert.Equal(TimeSpan.FromSeconds(10), (track with { IsPlaying = false }).PositionAt(Tracks.T0.AddSeconds(5)));
    }
}
