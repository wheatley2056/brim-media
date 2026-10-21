using System.Net;
using BrimMedia.Core;

namespace BrimMedia.Tests;

public class LrclibClientTests
{
    [Theory]
    [InlineData("Queen - Bohemian Rhapsody (Official Video Remastered)", "", "Bohemian Rhapsody", "Queen")]
    [InlineData("Bohemian Rhapsody [Lyrics]", "Queen - Topic", "Bohemian Rhapsody", "Queen")]
    [InlineData("Song (feat. Someone)", "Artist", "Song (feat. Someone)", "Artist")]
    public void CleansTitlesFromBrowsers(string title, string artist, string expectedTitle, string expectedArtist)
    {
        Assert.Equal((expectedTitle, expectedArtist), LrclibClient.Clean(title, artist));
    }

    [Fact]
    public async Task ExactMatchWithSyncedLyrics()
    {
        var http = new FakeHttp
        {
            Respond = r => r.RequestUri!.AbsolutePath == "/api/get"
                ? FakeHttp.Json("""{"trackName":"Bohemian Rhapsody","duration":354,"instrumental":false,"plainLyrics":"x","syncedLyrics":"[00:01.00]Is this the real life?"}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var client = new LrclibClient(new HttpClient(http));

        var lyrics = await client.GetAsync(Tracks.Spotify(), default);

        Assert.True(lyrics.IsSynced);
        var query = http.Requests[0].RequestUri!.Query;
        Assert.Contains("track_name=Bohemian%20Rhapsody", query);
        Assert.Contains("artist_name=Queen", query);
        Assert.Contains("duration=354", query);

        // Zweites Mal aus dem Speicher
        await client.GetAsync(Tracks.Spotify(), default);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task FallsBackToSearchAndPrefersMatchingLength()
    {
        var http = new FakeHttp
        {
            Respond = r => r.RequestUri!.AbsolutePath == "/api/search"
                ? FakeHttp.Json("""
                    [{"duration":200,"syncedLyrics":"[00:01.00]wrong"},
                     {"duration":359,"syncedLyrics":"[00:01.00]right"}]
                    """)
                : new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var client = new LrclibClient(new HttpClient(http));

        var lyrics = await client.GetAsync(Tracks.Browser(), default);

        Assert.Equal("right", lyrics.Synced[0].Text);
        Assert.Equal("/api/get", http.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/api/search", http.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task NothingFoundIsEmpty()
    {
        var http = new FakeHttp { Respond = r => r.RequestUri!.AbsolutePath == "/api/search" ? FakeHttp.Json("[]") : new HttpResponseMessage(HttpStatusCode.NotFound) };

        var lyrics = await new LrclibClient(new HttpClient(http)).GetAsync(Tracks.Spotify(), default);

        Assert.True(lyrics.IsEmpty);
    }

    [Fact]
    public async Task ServerErrorsThrow()
    {
        var http = new FakeHttp { Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };

        await Assert.ThrowsAsync<HttpRequestException>(() => new LrclibClient(new HttpClient(http)).GetAsync(Tracks.Spotify(), default));
    }

    [Fact]
    public void InstrumentalWithoutText()
    {
        var lyrics = LrclibClient.ToLyrics(new LrclibRecord { Instrumental = true });

        Assert.True(lyrics.Instrumental);
        Assert.False(lyrics.IsEmpty);
    }
}
