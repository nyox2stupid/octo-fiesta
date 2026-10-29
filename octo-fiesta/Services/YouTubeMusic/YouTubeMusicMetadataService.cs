using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Services.YouTubeMusic;

/// <summary>
/// YouTube Music search and metadata backed by yt-dlp.
/// Search uses YouTube Music's song/album/artist/playlist sections; track IDs are
/// YouTube video IDs so playback does not depend on a mutable public permalink.
/// </summary>
public sealed class YouTubeMusicMetadataService : IMusicMetadataService
{
    private const string Provider = "youtube";
    private static readonly Regex VideoIdPattern = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
    private static readonly Regex AlbumBrowseIdPattern = new("^(MPRE|MPED|MPLA)[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Regex ArtistBrowseIdPattern = new("^(MPAD|UC)[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new();

        try
        {
            var entries = await SearchEntriesAsync(query, "songs", limit);
            return entries
                .Select(entry => ParseSong(entry))
                .Where(song => song != null)
                .Cast<Song>()
                .GroupBy(song => song.ExternalId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(Math.Clamp(limit, 1, 50))
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new();

        try
        {
            var entries = await SearchEntriesAsync(query, "albums", limit, flatPlaylist: false);
            return entries
                .Select(entry => ParseAlbum(entry, null, null))
                .Where(album => album != null)
                .Cast<Album>()
                .GroupBy(album => album.ExternalId ?? album.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(Math.Clamp(limit, 1, 50))
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new();

        try
        {
            var entries = await SearchEntriesAsync(query, "artists", limit, flatPlaylist: false);
            return entries
                .Select(ParseArtist)
                .Where(artist => artist != null)
                .Cast<Artist>()
                .GroupBy(artist => artist.ExternalId ?? artist.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(Math.Clamp(limit, 1, 50))
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    public async Task<SearchResult> SearchAllAsync(
        string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        if (string.IsNullOrWhiteSpace(query) ||
            (songLimit <= 0 && albumLimit <= 0 && artistLimit <= 0))
            return new();

        var songsTask = songLimit > 0 ? SearchSongsAsync(query, songLimit) : Task.FromResult(new List<Song>());
        var albumsTask = albumLimit > 0 ? SearchAlbumsAsync(query, albumLimit) : Task.FromResult(new List<Album>());
        var artistsTask = artistLimit > 0 ? SearchArtistsAsync(query, artistLimit) : Task.FromResult(new List<Artist>());
        await Task.WhenAll(songsTask, albumsTask, artistsTask);

        return new SearchResult
        {
            Songs = await songsTask,
            Albums = await albumsTask,
            Artists = await artistsTask
        };
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (!IsProvider(externalProvider)) return null;

        try
        {
            var videoId = TryGetVideoId(Decode(externalId));
            if (videoId == null) return null;

            var url = BuildWatchUrl(videoId);
            var json = await RunYtDlpAsync(new[]
            {
                "--no-warnings", "--no-playlist", "--dump-single-json", "--skip-download", url
            });
            using var doc = JsonDocument.Parse(json);
            var song = ParseSong(doc.RootElement);
            if (song != null)
            {
                song.ExternalId = videoId;
                if (string.IsNullOrWhiteSpace(song.ExternalProvider))
                    song.ExternalProvider = Provider;
            }
            return song;
        }
        catch
        {
            return null;
        }
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (!IsProvider(externalProvider)) return null;

        try
        {
            var url = NormalizeCollectionUrl(Decode(externalId));
            if (url == null) return null;

            var (root, entries) = await GetCollectionPageAsync(url, flatPlaylist: true);
            var album = ParseAlbum(root, url, null);
            if (album == null) return null;

            album.Songs = entries
                .Select(entry => ParseSong(entry, album))
                .Where(song => song != null)
                .Cast<Song>()
                .Select((song, index) =>
                {
                    if (!song.Track.HasValue) song.Track = index + 1;
                    song.Album = album.Title;
                    song.AlbumId = album.Id;
                    song.AlbumArtist ??= album.Artist;
                    return song;
                })
                .ToList();

            if (album.Songs.Count > 0)
                album.SongCount = album.Songs.Count;
            return album;
        }
        catch
        {
            return null;
        }
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
    {
        if (!IsProvider(externalProvider)) return null;

        var url = NormalizeArtistUrl(Decode(externalId));
        if (url == null) return null;

        try
        {
            var json = await RunYtDlpAsync(new[]
            {
                "--no-warnings", "--flat-playlist", "--dump-single-json", "--skip-download", url
            });
            using var doc = JsonDocument.Parse(json);
            var artist = ParseArtist(doc.RootElement, url);
            if (artist != null) return artist;
        }
        catch
        {
            // Some YouTube Music artist browse pages are not standalone yt-dlp resources.
        }

        return new Artist
        {
            Id = ArtistId(url),
            Name = GetNameFromUrl(url),
            IsLocal = false,
            ExternalProvider = Provider,
            ExternalId = Encode(url)
        };
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
        if (!IsProvider(externalProvider)) return new();

        var artist = await GetArtistAsync(externalProvider, externalId);
        if (artist == null || string.IsNullOrWhiteSpace(artist.Name)) return new();

        var albums = await SearchAlbumsAsync(artist.Name, 50);
        return albums
            .Where(album =>
                !string.IsNullOrWhiteSpace(album.Artist) &&
                (album.Artist.Contains(artist.Name, StringComparison.OrdinalIgnoreCase) ||
                 artist.Name.Contains(album.Artist, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new();

        try
        {
            var entries = await SearchEntriesAsync(query, "community playlists", Math.Clamp(limit * 2, 10, 50));
            if (entries.Count < limit)
            {
                var featured = await SearchEntriesAsync(query, "featured playlists", Math.Clamp(limit * 2, 10, 50));
                entries.AddRange(featured);
            }

            return entries
                .Select(entry => ParsePlaylist(entry, null))
                .Where(playlist => playlist != null)
                .Cast<ExternalPlaylist>()
                .GroupBy(playlist => playlist.ExternalId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(Math.Clamp(limit, 1, 50))
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
    {
        if (!IsProvider(externalProvider)) return null;

        try
        {
            var url = NormalizeCollectionUrl(Decode(externalId));
            if (url == null) return null;

            var (root, entries) = await GetCollectionPageAsync(url, flatPlaylist: true);
            return ParsePlaylist(root, url, entries.Count);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
    {
        if (!IsProvider(externalProvider)) return new();

        try
        {
            var url = NormalizeCollectionUrl(Decode(externalId));
            if (url == null) return new();

            var (root, entries) = await GetCollectionPageAsync(url, flatPlaylist: true);
            var playlistName = GetString(root, "title") ?? "YouTube Music playlist";
            var playlistId = PlaylistIdHelper.CreatePlaylistId(Provider, Encode(url));

            return entries
                .Select(entry => ParseSong(entry))
                .Where(song => song != null)
                .Cast<Song>()
                .Select((song, index) =>
                {
                    song.Album = playlistName;
                    song.AlbumId = playlistId;
                    if (!song.Track.HasValue) song.Track = index + 1;
                    return song;
                })
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    private static async Task<List<JsonElement>> SearchEntriesAsync(string query, string section, int limit, bool flatPlaylist = true)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new();

        var url = BuildSearchUrl(query, section);
        var args = new List<string> { "--ignore-errors", "--no-warnings" };
        // Song and playlist search results carry useful metadata when flat. Album and
        // artist search results are only browse URLs, so resolve them for their titles
        // and artist metadata instead of accepting the incomplete flat entries.
        if (flatPlaylist)
            args.Add("--flat-playlist");
        args.Add("--playlist-end");
        args.Add(Math.Clamp(limit, 1, 50).ToString(System.Globalization.CultureInfo.InvariantCulture));
        args.Add("--dump-single-json");
        args.Add("--skip-download");
        args.Add(url);

        var json = await RunYtDlpAsync(args);
        using var doc = JsonDocument.Parse(json);
        return ReadEntries(doc.RootElement);
    }

    internal static string BuildSearchUrl(string query, string section) =>
        $"https://music.youtube.com/search?q={Uri.EscapeDataString(query)}#{Uri.EscapeDataString(section)}";

    private static async Task<(JsonElement Root, List<JsonElement> Entries)> GetCollectionPageAsync(
        string url, bool flatPlaylist)
    {
        var args = new List<string> { "--ignore-errors", "--no-warnings" };
        if (flatPlaylist)
            args.Add("--flat-playlist");
        args.Add("--dump-single-json");
        args.Add("--skip-download");
        args.Add(url);

        var json = await RunYtDlpAsync(args);
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.Clone(), ReadEntries(doc.RootElement));
    }

    private static List<JsonElement> ReadEntries(JsonElement root)
    {
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return new();

        return entries.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry => entry.Clone())
            .ToList();
    }

    internal static Song? ParseSong(JsonElement item, Album? parentAlbum = null)
    {
        var videoId = TryGetVideoId(GetEntryUrl(item)) ?? TryGetVideoId(GetString(item, "id"));
        var title = GetString(item, "title") ?? GetString(item, "track");
        if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(title))
            return null;

        var artist = GetArtistName(item) ?? parentAlbum?.Artist ?? "YouTube";
        var artistUrl = GetArtistUrl(item);
        var artistId = artistUrl == null ? parentAlbum?.ArtistId : ArtistId(artistUrl);
        var thumbnail = GetThumbnail(item);
        var albumName = parentAlbum?.Title
            ?? GetNestedString(item, "album", "name")
            ?? GetNestedString(item, "album", "title")
            ?? GetString(item, "album")
            ?? "YouTube Music";
        var albumUrl = parentAlbum?.ExternalId != null
            ? NormalizeCollectionUrl(Decode(parentAlbum.ExternalId))
            : NormalizeCollectionUrl(
                GetString(item, "album_url")
                ?? GetNestedString(item, "album", "url")
                ?? GetNestedString(item, "album", "webpage_url")
                ?? UrlFromResourceId(GetNestedString(item, "album", "id")));
        var duration = ReadInt(item, "duration");
        var year = ReadYear(item);
        var artistLinks = new List<Artist>();
        if (artistUrl != null)
        {
            artistLinks.Add(new Artist
            {
                Id = ArtistId(artistUrl),
                Name = artist,
                IsLocal = false,
                ExternalProvider = Provider,
                ExternalId = Encode(artistUrl)
            });
        }

        return new Song
        {
            Title = title,
            Artist = artist,
            ArtistId = artistId,
            Artists = artistLinks,
            Album = albumName,
                        AlbumId = parentAlbum?.Id ?? (albumUrl == null ? null : AlbumId(albumUrl)),
            AlbumArtist = parentAlbum?.Artist ?? GetString(item, "album_artist") ?? artist,
            Duration = duration,
            Track = ReadInt(item, "track_number") ?? ReadInt(item, "track"),
            DiscNumber = ReadInt(item, "disc_number") ?? 1,
            Year = year,
            ReleaseDate = GetString(item, "release_date"),
            Genre = GetString(item, "genre"),
            CoverArtUrl = thumbnail,
            CoverArtUrlLarge = thumbnail,
            IsLocal = false,
            ExternalProvider = Provider,
            ExternalId = videoId,
            ReleaseType = GetString(item, "album_type")
        };
    }

    internal static Album? ParseAlbum(JsonElement item, string? fallbackUrl, string? fallbackArtist)
    {
        var url = NormalizeCollectionUrl(GetEntryUrl(item))
            ?? NormalizeCollectionUrl(fallbackUrl)
            ?? NormalizeCollectionUrl(UrlFromResourceId(GetString(item, "id")));
        var title = GetString(item, "title") ?? GetString(item, "album") ?? GetString(item, "playlist_title");
        if (url == null || string.IsNullOrWhiteSpace(title))
            return null;

        var artist = GetArtistName(item) ?? fallbackArtist ?? "YouTube Music";
        var encodedUrl = Encode(url);
        return new Album
        {
            Id = AlbumId(url),
            Title = title,
            Artist = artist,
            ArtistId = GetArtistUrl(item) is string artistUrl ? ArtistId(artistUrl) : null,
            Year = ReadYear(item),
            SongCount = ReadInt(item, "playlist_count") ?? ReadInt(item, "track_count") ?? ReadEntryCount(item),
            ReleaseType = GetString(item, "album_type") ?? "album",
            CoverArtUrl = GetThumbnail(item),
            CoverArtUrlLarge = GetThumbnail(item),
            Genre = GetString(item, "genre"),
            IsLocal = false,
            ExternalProvider = Provider,
            ExternalId = encodedUrl
        };
    }

    internal static Artist? ParseArtist(JsonElement item, string? fallbackUrl = null)
    {
        var url = NormalizeArtistUrl(GetArtistUrl(item))
            ?? NormalizeArtistUrl(fallbackUrl)
            ?? NormalizeArtistUrl(UrlFromResourceId(GetString(item, "browse_id") ?? GetString(item, "browseId") ?? GetString(item, "id")));
        var name = GetArtistName(item) ?? GetString(item, "title") ?? GetString(item, "name");
        if (url == null || string.IsNullOrWhiteSpace(name))
            return null;

        return new Artist
        {
            Id = ArtistId(url),
            Name = name,
            ImageUrl = GetThumbnail(item),
            AlbumCount = ReadInt(item, "album_count"),
            IsLocal = false,
            ExternalProvider = Provider,
            ExternalId = Encode(url)
        };
    }

    internal static ExternalPlaylist? ParsePlaylist(JsonElement item, string? fallbackUrl, int? fallbackCount = null)
    {
        var url = NormalizeCollectionUrl(GetEntryUrl(item))
            ?? NormalizeCollectionUrl(fallbackUrl)
            ?? NormalizeCollectionUrl(UrlFromResourceId(GetString(item, "id")));
        var title = GetString(item, "title") ?? GetString(item, "playlist_title");
        if (url == null || string.IsNullOrWhiteSpace(title)) return null;

        var encodedUrl = Encode(url);
        var count = ReadInt(item, "playlist_count") ?? fallbackCount ??
                    (item.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
                        ? entries.GetArrayLength() : 0);
        return new ExternalPlaylist
        {
            Id = PlaylistIdHelper.CreatePlaylistId(Provider, encodedUrl),
            Name = title,
            Description = GetString(item, "description"),
            CuratorName = GetArtistName(item),
            Provider = Provider,
            ExternalId = encodedUrl,
            TrackCount = count,
            Duration = ReadInt(item, "duration") ?? 0,
            CoverUrl = GetThumbnail(item),
            CreatedDate = ReadDate(item)
        };
    }

    private static string? GetArtistName(JsonElement item)
    {
        var direct = GetString(item, "artist")
            ?? GetString(item, "album_artist")
            ?? GetString(item, "uploader")
            ?? GetString(item, "channel")
            ?? GetString(item, "creator")
            ?? GetNestedString(item, "channel", "name")
            ?? GetNestedString(item, "uploader", "name");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        foreach (var property in new[] { "artists", "artist" })
        {
            if (!item.TryGetProperty(property, out var artists) || artists.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var entry in artists.EnumerateArray())
            {
                var name = entry.ValueKind == JsonValueKind.String ? entry.GetString() :
                    GetString(entry, "name") ?? GetString(entry, "title");
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        return null;
    }

    private static string? GetArtistUrl(JsonElement item)
    {
        foreach (var property in new[] { "artist_url", "uploader_url", "channel_url", "url", "webpage_url" })
        {
            var candidate = GetString(item, property);
            if (NormalizeArtistUrl(candidate) != null)
                return NormalizeArtistUrl(candidate);
        }

        var id = GetString(item, "browse_id") ?? GetString(item, "browseId");
        return NormalizeArtistUrl(UrlFromResourceId(id));
    }

    private static string? GetEntryUrl(JsonElement item) =>
        GetString(item, "webpage_url")
        ?? GetString(item, "original_url")
        ?? GetString(item, "url");

    private static string? GetThumbnail(JsonElement item)
    {
        var thumbnail = GetString(item, "thumbnail") ?? GetString(item, "artwork_url");
        if (!string.IsNullOrWhiteSpace(thumbnail)) return thumbnail;

        if (item.TryGetProperty("thumbnails", out var thumbnails) && thumbnails.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in thumbnails.EnumerateArray().Reverse())
            {
                var url = GetString(candidate, "url");
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetNestedString(JsonElement item, string parent, string property) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(parent, out var nested) &&
        nested.ValueKind == JsonValueKind.Object
            ? GetString(nested, property)
            : null;

    private static int? ReadInt(JsonElement item, string property)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        return null;
    }

    private static int? ReadEntryCount(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty("entries", out var entries) &&
        entries.ValueKind == JsonValueKind.Array
            ? entries.GetArrayLength()
            : null;

    private static int? ReadYear(JsonElement item)
    {
        var releaseDate = GetString(item, "release_date") ?? GetString(item, "upload_date");
        if (releaseDate is { Length: >= 4 } && int.TryParse(releaseDate[..4], out var year))
            return year;

        if (item.TryGetProperty("release_timestamp", out var timestamp) &&
            timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds).Year; } catch { }
        }
        return null;
    }

    private static DateTime? ReadDate(JsonElement item)
    {
        if (item.TryGetProperty("release_timestamp", out var timestamp) &&
            timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; } catch { }
        }

        var date = GetString(item, "upload_date") ?? GetString(item, "release_date");
        return DateTime.TryParse(date, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    private static string? NormalizeCollectionUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsYouTubeHost(uri.Host))
            return null;

        if (uri.AbsolutePath.Equals("/playlist", StringComparison.OrdinalIgnoreCase))
            return GetQueryValue(uri, "list") is { Length: > 0 } ? uri.ToString() : null;

        if (uri.AbsolutePath.StartsWith("/browse/", StringComparison.OrdinalIgnoreCase))
            return uri.ToString();

        return null;
    }

    private static string? NormalizeArtistUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsYouTubeHost(uri.Host))
            return null;

        if (uri.AbsolutePath.StartsWith("/channel/", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.StartsWith("/@/", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(uri.AbsolutePath, @"/@[^/]+", RegexOptions.IgnoreCase) ||
            uri.AbsolutePath.StartsWith("/user/", StringComparison.OrdinalIgnoreCase))
        {
            return uri.ToString();
        }

        if (uri.AbsolutePath.StartsWith("/browse/", StringComparison.OrdinalIgnoreCase))
        {
            var browseId = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
            // Album browse IDs also live under /browse/, but are not artist pages.
            if (!string.IsNullOrWhiteSpace(browseId) && !AlbumBrowseIdPattern.IsMatch(browseId))
                return uri.ToString();
        }

        return null;
    }

    private static string? UrlFromResourceId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (AlbumBrowseIdPattern.IsMatch(id))
            return $"https://music.youtube.com/browse/{Uri.EscapeDataString(id)}";
        if (id.StartsWith("OLAK5uy_", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("PL", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("RD", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("VL", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://music.youtube.com/playlist?list={Uri.EscapeDataString(id)}";
        }
        if (ArtistBrowseIdPattern.IsMatch(id))
            return $"https://music.youtube.com/browse/{Uri.EscapeDataString(id)}";
        return null;
    }

    private static string? TryGetVideoId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (VideoIdPattern.IsMatch(value)) return value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsYouTubeHost(uri.Host))
            return null;

        var path = uri.AbsolutePath.Trim('/');
        if (path.Equals("watch", StringComparison.OrdinalIgnoreCase))
        {
            var videoId = GetQueryValue(uri, "v");
            return videoId != null && VideoIdPattern.IsMatch(videoId) ? videoId : null;
        }

        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("shorts/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("embed/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("live/", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = path.Split('/').LastOrDefault();
            return candidate != null && VideoIdPattern.IsMatch(candidate) ? candidate : null;
        }

        return null;
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]).Equals(key, StringComparison.OrdinalIgnoreCase))
                return parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace("+", " ")) : string.Empty;
        }
        return null;
    }

    private static bool IsYouTubeHost(string host) =>
        host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);

    private static string BuildWatchUrl(string videoId) =>
        $"https://music.youtube.com/watch?v={Uri.EscapeDataString(videoId)}";

    private static string? GetNameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var last = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(last))
                return Uri.UnescapeDataString(last).Replace('_', ' ').Replace('-', ' ');
        }
        return "YouTube Music";
    }

    private static bool IsProvider(string? provider) =>
        string.Equals(provider, Provider, StringComparison.OrdinalIgnoreCase);

    private static string ArtistId(string url) => $"ext-{Provider}-artist-{Encode(url)}";
    private static string AlbumId(string url) => $"ext-{Provider}-album-{Encode(url)}";
    private static string Encode(string value) => Uri.EscapeDataString(value);
    private static string Decode(string value) => Uri.UnescapeDataString(value);

    private static async Task<string> RunYtDlpAsync(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start yt-dlp.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("yt-dlp did not respond within 60 seconds.");
        }

        var output = await stdoutTask;
        var error = await stderrTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"yt-dlp failed (exit {process.ExitCode}): {error}");

        return output;
    }
}