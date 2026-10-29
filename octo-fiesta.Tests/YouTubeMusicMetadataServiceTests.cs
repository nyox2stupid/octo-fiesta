using System.Text.Json;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.YouTubeMusic;

namespace octo_fiesta.Tests;

public class YouTubeMusicMetadataServiceTests
{
    [Theory]
    [InlineData("songs")]
    [InlineData("albums")]
    [InlineData("artists")]
    [InlineData("community playlists")]
    public void BuildSearchUrl_UsesYouTubeMusicSection(string section)
    {
        var url = YouTubeMusicMetadataService.BuildSearchUrl("hello world", section);

        Assert.StartsWith("https://music.youtube.com/search?q=hello%20world#", url);
        Assert.EndsWith(Uri.EscapeDataString(section), url);
    }

    [Fact]
    public void ParseSong_FromFlatSearchEntry_UsesVideoIdAsExternalId()
    {
        var item = ParseJson("""
            {
              "id": "dQw4w9WgXcQ",
              "title": "Test Song",
              "uploader": "Example Artist",
              "duration": 213,
              "thumbnails": [{ "url": "https://example.test/cover.jpg" }]
            }
            """);

        var song = YouTubeMusicMetadataService.ParseSong(item);

        Assert.NotNull(song);
        Assert.Equal("Test Song", song.Title);
        Assert.Equal("Example Artist", song.Artist);
        Assert.Equal(213, song.Duration);
        Assert.Equal("dQw4w9WgXcQ", song.ExternalId);
        Assert.Equal("youtube", song.ExternalProvider);
        Assert.Equal("https://example.test/cover.jpg", song.CoverArtUrl);
        Assert.False(song.IsLocal);
    }

    [Fact]
    public void ParseSong_FromWatchUrl_ExtractsVideoId()
    {
        var item = ParseJson("""
            {
              "webpage_url": "https://music.youtube.com/watch?v=dQw4w9WgXcQ",
              "title": "Test Song",
              "channel": "Example Artist"
            }
            """);

        var song = YouTubeMusicMetadataService.ParseSong(item);

        Assert.NotNull(song);
        Assert.Equal("dQw4w9WgXcQ", song.ExternalId);
        Assert.Equal("Example Artist", song.Artist);
    }

    [Fact]
    public void ParseAlbum_UsesStableMusicBrowseUrl()
    {
        const string albumUrl = "https://music.youtube.com/browse/MPREb_example123";
        var item = ParseJson("""
            {
              "id": "MPREb_example123",
              "url": "https://music.youtube.com/browse/MPREb_example123",
              "title": "Example Album",
              "artists": [{ "name": "Example Artist" }],
              "playlist_count": 9,
              "thumbnails": [{ "url": "https://example.test/album.jpg" }]
            }
            """);

        var album = YouTubeMusicMetadataService.ParseAlbum(item, null, null);

        Assert.NotNull(album);
        Assert.Equal("Example Album", album.Title);
        Assert.Equal("Example Artist", album.Artist);
        Assert.Equal(9, album.SongCount);
        Assert.Equal("youtube", album.ExternalProvider);
        Assert.Equal(Uri.EscapeDataString(albumUrl), album.ExternalId);
        Assert.Equal("https://example.test/album.jpg", album.CoverArtUrl);
        Assert.StartsWith("ext-youtube-album-", album.Id);
    }

    [Fact]
    public void ParsePlaylist_UsesPlaylistIdHelperFormat()
    {
        var item = ParseJson("""
            {
              "id": "OLAK5uy_example123",
              "url": "https://music.youtube.com/playlist?list=OLAK5uy_example123",
              "title": "Example Playlist",
              "playlist_count": 12
            }
            """);

        var playlist = YouTubeMusicMetadataService.ParsePlaylist(item, null);

        Assert.NotNull(playlist);
        Assert.Equal("Example Playlist", playlist.Name);
        Assert.Equal(12, playlist.TrackCount);
        Assert.Equal("youtube", playlist.Provider);
        Assert.Equal("pl-youtube-" + Uri.EscapeDataString("https://music.youtube.com/playlist?list=OLAK5uy_example123"), playlist.Id);
        Assert.True(PlaylistIdHelper.IsExternalPlaylist(playlist.Id));
        Assert.Equal("youtube", PlaylistIdHelper.ParsePlaylistId(playlist.Id).provider);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}