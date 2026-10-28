using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Subsonic;
using octo_fiesta.Services.Common;

namespace octo_fiesta.Services.SoundCloud;

/// <summary>
/// SoundCloud search and metadata through yt-dlp. Artist cards are derived from
/// matching track uploaders; album and set cards come from uploader collection pages.
/// </summary>
public sealed class SoundCloudMetadataService : IMusicMetadataService
{
    private const string Provider = "soundcloud";
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _clientIdLock = new(1, 1);
    private string? _clientId;

    public SoundCloudMetadataService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new();
        var entries = await SearchEntriesAsync(query, limit);
        return entries.Select(ParseSong).Where(song => song != null).Cast<Song>().ToList();
    }

    public async Task<SearchResult> SearchAllAsync(
        string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        if (string.IsNullOrWhiteSpace(query) ||
            (songLimit <= 0 && albumLimit <= 0 && artistLimit <= 0))
            return new();

        // Use each category's own search method. In particular, albums must go
        // through SearchAlbumsAsync so the direct SoundCloud playlist/set search
        // runs instead of relying only on artists discovered via track search.
        var songsTask = songLimit > 0
            ? SearchSongsAsync(query, songLimit)
            : Task.FromResult(new List<Song>());
        var albumsTask = albumLimit > 0
            ? SearchAlbumsAsync(query, albumLimit)
            : Task.FromResult(new List<Album>());
        var artistsTask = artistLimit > 0
            ? SearchArtistsAsync(query, artistLimit)
            : Task.FromResult(new List<Artist>());

        await Task.WhenAll(songsTask, albumsTask, artistsTask);

        return new SearchResult
        {
            Songs = await songsTask,
            Albums = await albumsTask,
            Artists = await artistsTask
        };
    }
    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new();
        var entries = await SearchEntriesAsync(query, Math.Clamp(limit * 3, 10, 50));
        return BuildArtists(entries, limit);
    }

    private static List<Artist> BuildArtists(IEnumerable<JsonElement> entries, int limit) =>
        ExtractProfiles(entries)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(profile => new Artist
            {
                Id = ArtistId(profile.Url),
                Name = profile.Name,
                ImageUrl = profile.ImageUrl,
                IsLocal = false,
                ExternalProvider = Provider,
                ExternalId = EncodeUrl(profile.Url)
            })
            .ToList();

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new();

        // Search albums directly. Track-search-derived uploader lists can miss albums
        // when none of the tracks happen to match the album title.
        try
        {
            // SoundCloud exposes playlist/set search, not a dedicated public album-search endpoint.
            // Albums are sets whose metadata marks set_type as album/ep/single.
            var albumEntries = await SearchSoundCloudCollectionAsync(
                "search/playlists", query, Math.Clamp(limit * 3, 10, 50));
            var albums = albumEntries
                .Where(IsAlbumEntry)
                .Select(entry =>
                {
                    var album = ParseAlbum(entry, GetEntryUrl(entry),
                        GetString(entry, "uploader") ?? GetNestedString(entry, "user", "username"));
                    var playlistApiUrl = GetSoundCloudPlaylistApiUrl(entry);
                    if (album != null && playlistApiUrl != null)
                    {
                        // Search results can contain stale SoundCloud permalinks after a
                        // profile slug changes. Keep the stable playlist ID in the Subsonic
                        // album ID and fetch details through yt-dlp's playlist API extractor.
                        album.Id = AlbumId(playlistApiUrl);
                        album.ExternalId = EncodeUrl(playlistApiUrl);
                    }
                    return album;
                })
                .Where(album => album != null)
                .Cast<Album>()
                .GroupBy(album => album.ExternalId ?? album.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(Math.Clamp(limit, 1, 50))
                .ToList();

            if (albums.Count > 0) return albums;
        }
        catch
        {
            // Fall back to profile album pages if the search endpoint changes or is unavailable.
        }

        var entries = await SearchEntriesAsync(query, Math.Clamp(limit * 3, 10, 50));
        return await SearchAlbumsFromProfilesAsync(query, limit, ExtractProfiles(entries));
    }

    private async Task<List<Album>> SearchAlbumsFromProfilesAsync(
        string query, int limit, List<Profile> discoveredProfiles)
    {
        var profiles = discoveredProfiles.Take(8).ToList();
        if (profiles.Count == 0 || limit <= 0) return new();

        var collections = await Task.WhenAll(profiles.Select(async profile =>
        {
            try
            {
                var (_, albumEntries) = await GetCollectionPageAsync(CollectionUrl(profile.Url, "albums"));
                return albumEntries
                    .Select(entry => ParseAlbum(entry, GetEntryUrl(entry), profile.Name))
                    .Where(album => album != null)
                    .Cast<Album>()
                    .Where(album => IsRelevant(query, album.Title, profile.Name))
                    .ToList();
            }
            catch
            {
                return new List<Album>();
            }
        }));

        var results = new List<Album>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in collections.SelectMany(collection => collection))
        {
            if (seen.Add(album.ExternalId ?? album.Id))
                results.Add(album);
            if (results.Count >= Math.Clamp(limit, 1, 50)) break;
        }
        return results;
    }

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new();

        var entries = await SearchEntriesAsync(query, Math.Clamp(limit * 3, 10, 50));
        var profiles = ExtractProfiles(entries).Take(8).ToList();
        if (profiles.Count == 0) return new();

        var collections = await Task.WhenAll(profiles.Select(async profile =>
        {
            try
            {
                var (_, playlistEntries) = await GetCollectionPageAsync(CollectionUrl(profile.Url, "sets"));
                return playlistEntries
                    .Select(entry => ParsePlaylist(entry, GetEntryUrl(entry), profile.Name))
                    .Where(playlist => playlist != null)
                    .Cast<ExternalPlaylist>()
                    .Where(playlist => IsRelevant(query, playlist.Name, profile.Name))
                    .ToList();
            }
            catch
            {
                return new List<ExternalPlaylist>();
            }
        }));

        var results = new List<ExternalPlaylist>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in collections.SelectMany(collection => collection))
        {
            if (seen.Add(playlist.ExternalId))
                results.Add(playlist);
            if (results.Count >= Math.Clamp(limit, 1, 50)) break;
        }
        return results;
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (!IsProvider(externalProvider)) return null;
        try
        {
            var url = DecodeUrl(externalId);
            if (!IsSoundCloudUrl(url)) return null;

            var json = await RunYtDlpAsync(new[]
            {
                "--no-warnings", "--dump-single-json", "--skip-download", url
            });
            using var doc = JsonDocument.Parse(json);
            var song = ParseSong(doc.RootElement);
            if (song != null)
            {
                // Preserve the exact identifier supplied by the client. For album
                // tracks this is the stable SoundCloud track API URL, not a permalink
                // that may have gone stale after an uploader/profile rename.
                song.ExternalId = EncodeUrl(url);
            }
            return song;
        }
        catch
        {
            return null;
        }
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
    {
        if (!IsProvider(externalProvider)) return null;
        try
        {
            var url = DecodeUrl(externalId);
            if (!IsSoundCloudProfileUrl(url)) return null;

            var json = await RunYtDlpAsync(new[]
            {
                "--no-warnings", "--flat-playlist", "--dump-single-json", "--skip-download", url
            });
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var name = GetString(root, "uploader")
                ?? GetString(root, "artist")
                ?? StripCollectionSuffix(GetString(root, "title"))
                ?? ProfileNameFromUrl(url);

            return new Artist
            {
                Id = ArtistId(url),
                Name = name,
                ImageUrl = GetThumbnail(root),
                IsLocal = false,
                ExternalProvider = Provider,
                ExternalId = EncodeUrl(url)
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
        if (!IsProvider(externalProvider)) return new();

        try
        {
            var profileUrl = DecodeUrl(externalId);
            if (!IsSoundCloudProfileUrl(profileUrl)) return new();

            var profile = ProfileNameFromUrl(profileUrl);
            var (_, entries) = await GetCollectionPageAsync(CollectionUrl(profileUrl, "albums"));
            return entries
                .Select(entry => ParseAlbum(entry, GetEntryUrl(entry), profile))
                .Where(album => album != null)
                .Cast<Album>()
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (!IsProvider(externalProvider)) return null;
        try
        {
            var url = DecodeUrl(externalId);
            if (!IsSoundCloudSetUrl(url)) return null;

            // Album detail needs resolved track metadata (not flat playlist stubs).
            // Flat entries can omit the URL/fields that ParseSong needs, producing a
            // valid album response with an empty song list in Subsonic clients.
            var (root, entries) = await GetCollectionPageAsync(url, flatPlaylist: false);
            var album = ParseAlbum(root, url, null);
            if (album == null) return null;

            album.Songs = entries
                .Select(entry =>
                {
                    var song = ParseSong(entry);
                    var trackApiUrl = GetSoundCloudTrackApiUrl(entry);
                    if (song != null && trackApiUrl != null)
                        song.ExternalId = EncodeUrl(trackApiUrl);
                    return song;
                })
                .Where(song => song != null)
                .Cast<Song>()
                .ToList();

            foreach (var song in album.Songs)
            {
                song.Album = album.Title;
                song.AlbumId = album.Id;
                if (string.IsNullOrEmpty(song.Artist)) song.Artist = album.Artist;
                if (string.IsNullOrEmpty(song.ArtistId)) song.ArtistId = album.ArtistId;
            }

            album.SongCount = album.Songs.Count;
            return album;
        }
        catch
        {
            return null;
        }
    }

    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
    {
        if (!IsProvider(externalProvider)) return null;
        try
        {
            var url = DecodeUrl(externalId);
            if (!IsSoundCloudSetUrl(url)) return null;

            var (root, _) = await GetCollectionPageAsync(url);
            return ParsePlaylist(root, url, null);
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
            var url = DecodeUrl(externalId);
            if (!IsSoundCloudSetUrl(url)) return new();

            // Resolve each track fully so playlist responses contain playable song metadata.
            var (root, entries) = await GetCollectionPageAsync(url, flatPlaylist: false);
            var playlistName = GetString(root, "title") ?? "SoundCloud playlist";
            var playlistId = PlaylistIdHelper.CreatePlaylistId(Provider, EncodeUrl(url));

            var songs = entries
                .Select(entry =>
                {
                    var song = ParseSong(entry);
                    var trackApiUrl = GetSoundCloudTrackApiUrl(entry);
                    if (song != null && trackApiUrl != null)
                        song.ExternalId = EncodeUrl(trackApiUrl);
                    return song;
                })
                .Where(song => song != null)
                .Cast<Song>()
                .ToList();

            foreach (var song in songs)
            {
                song.Album = playlistName;
                song.AlbumId = playlistId;
            }

            return songs;
        }
        catch
        {
            return new();
        }
    }

    private async Task<List<JsonElement>> SearchSoundCloudCollectionAsync(
        string endpoint, string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new();

        // Use yt-dlp's own SoundCloud extractor for API requests. SoundCloud can
        // reject ordinary HttpClient requests with 403 even while yt-dlp search
        // works, so a separately scraped client_id is not reliable here.
        var json = await RunSoundCloudCollectionSearchAsync(endpoint, query, Math.Clamp(limit, 1, 50));
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("collection", out var collection) ||
            collection.ValueKind != JsonValueKind.Array)
            return new();

        return collection.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => item.Clone())
            .ToList();
    }

    private static async Task<string> RunSoundCloudCollectionSearchAsync(
        string endpoint, string query, int limit)
    {
        // Reuse yt-dlp's initialized SoundCloud extractor so client-id discovery,
        // refresh-on-403 behavior, headers, and HTTP handling match scsearch.
        const string script = """
import json
import sys
from yt_dlp import YoutubeDL
from yt_dlp.extractor.soundcloud import SoundcloudSearchIE

endpoint, query, raw_limit = sys.argv[1:4]
limit = min(max(int(raw_limit), 1), 50)

with YoutubeDL({"quiet": True, "no_warnings": True}) as ydl:
    ie = SoundcloudSearchIE(ydl)
    ie.initialize()
    response = ie._call_api(
        f"https://api-v2.soundcloud.com/{endpoint.lstrip('/')}",
        query,
        "Searching SoundCloud collections",
        query={
            "q": query,
            "limit": limit,
            "linked_partitioning": 1,
            "offset": 0,
        },
        headers=ie._HEADERS,
    )
    print(json.dumps(response))
""";

        var start = new ProcessStartInfo
        {
            FileName = "python3",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(endpoint);
        start.ArgumentList.Add(query);
        start.ArgumentList.Add(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start Python for SoundCloud API search.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"SoundCloud playlist search failed: {error}");
        return output;
    }

    private async Task<string> GetClientIdAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(_clientId)) return _clientId!;

        await _clientIdLock.WaitAsync();
        try
        {
            if (!forceRefresh && !string.IsNullOrWhiteSpace(_clientId)) return _clientId!;

            var homepage = await _httpClient.GetStringAsync("https://soundcloud.com/");
            var scriptMatches = Regex.Matches(
                homepage, @"<script[^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);

            // yt-dlp uses the same approach: client IDs are embedded in SoundCloud JS bundles
            // and rotate periodically, so don't hard-code one.
            foreach (Match scriptMatch in scriptMatches.Cast<Match>().Reverse())
            {
                var scriptUrl = WebUtility.HtmlDecode(scriptMatch.Groups[1].Value);
                if (!Uri.TryCreate(new Uri("https://soundcloud.com/"), scriptUrl, out var absoluteScriptUrl))
                    continue;

                try
                {
                    var script = await _httpClient.GetStringAsync(absoluteScriptUrl);
                    var clientIdMatch = Regex.Match(
                        script, @"client_id\s*:\s*[""']([0-9a-zA-Z]{32})[""']",
                        RegexOptions.IgnoreCase);

                    if (clientIdMatch.Success)
                    {
                        _clientId = clientIdMatch.Groups[1].Value;
                        return _clientId;
                    }
                }
                catch
                {
                    // Some bundles may be unavailable; try the next one.
                }
            }

            throw new InvalidOperationException("Could not find a SoundCloud client ID in the site scripts.");
        }
        finally
        {
            _clientIdLock.Release();
        }
    }

    private async Task<List<JsonElement>> SearchEntriesAsync(string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return new();

        try
        {
            var json = await RunYtDlpAsync(new[]
            {
                "--ignore-errors", "--no-warnings", "--flat-playlist",
                "--dump-single-json", "--skip-download",
                $"scsearch{Math.Clamp(limit, 1, 50)}:{query}"
            });
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                return new();

            return entries.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.Object)
                .Select(entry => entry.Clone())
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    private async Task<(JsonElement Root, List<JsonElement> Entries)> GetCollectionPageAsync(
        string url, bool flatPlaylist = true)
    {
        if (!IsSoundCloudUrl(url))
            throw new InvalidOperationException("Invalid SoundCloud URL.");

        var args = new List<string> { "--ignore-errors", "--no-warnings" };
        if (flatPlaylist)
            args.Add("--flat-playlist");
        args.Add("--dump-single-json");
        args.Add("--skip-download");
        args.Add(url);

        var json = await RunYtDlpAsync(args);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        var entries = root.TryGetProperty("entries", out var entriesElement) &&
                      entriesElement.ValueKind == JsonValueKind.Array
            ? entriesElement.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.Object)
                .Select(entry => entry.Clone())
                .ToList()
            : new List<JsonElement>();

        return (root, entries);
    }

    private static List<Profile> ExtractProfiles(IEnumerable<JsonElement> entries)
    {
        var profiles = new List<Profile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var trackUrl = GetEntryUrl(entry);
            var url = GetString(entry, "uploader_url")
                ?? GetNestedString(entry, "user", "permalink_url")
                ?? ProfileUrlFromTrackUrl(trackUrl);
            var name = GetString(entry, "artist")
                ?? GetString(entry, "uploader")
                ?? GetNestedString(entry, "user", "username")
                ?? (url == null ? null : ProfileNameFromUrl(url));

            if (!IsSoundCloudProfileUrl(url) || string.IsNullOrWhiteSpace(name)) continue;
            var normalizedUrl = url!.TrimEnd('/');
            if (seen.Add(normalizedUrl))
                profiles.Add(new Profile(normalizedUrl, name, GetString(entry, "uploader_thumbnail")));
        }

        return profiles;
    }

    private static Song? ParseSong(JsonElement item)
    {
        var url = GetEntryUrl(item);
        var title = GetString(item, "track") ?? GetString(item, "title");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title) ||
            !IsSoundCloudUrl(url) || IsSoundCloudSetUrl(url))
            return null;

        var artist = GetString(item, "artist")
            ?? GetString(item, "uploader")
            ?? GetNestedString(item, "user", "username")
            ?? "Unknown Artist";
        var profileUrl = GetString(item, "uploader_url")
            ?? GetNestedString(item, "user", "permalink_url")
            ?? ProfileUrlFromTrackUrl(url);
        var artistId = IsSoundCloudProfileUrl(profileUrl) ? ArtistId(profileUrl!) : null;
        var encodedUrl = EncodeUrl(url);
        var uploadDate = GetString(item, "upload_date");
        int? year = uploadDate is { Length: >= 4 } && int.TryParse(uploadDate[..4], out var y) ? y : null;
        if (year == null && item.TryGetProperty("release_timestamp", out var releaseTime) &&
            releaseTime.ValueKind == JsonValueKind.Number &&
            releaseTime.TryGetInt64(out var timestamp))
        {
            try { year = DateTimeOffset.FromUnixTimeSeconds(timestamp).Year; } catch { }
        }

        int? duration = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(d.GetDouble()) : null;
        var thumbnail = GetThumbnail(item);
        var album = GetString(item, "album");

        return new Song
        {
            Title = title,
            Artist = artist,
            ArtistId = artistId,
            Artists = artistId == null ? new List<Artist>() : new List<Artist>
            {
                new() { Id = artistId, Name = artist, ExternalProvider = Provider, ExternalId = EncodeUrl(profileUrl!) }
            },
            Album = string.IsNullOrWhiteSpace(album) ? "SoundCloud" : album,
            Duration = duration,
            Year = year,
            CoverArtUrl = thumbnail,
            CoverArtUrlLarge = thumbnail,
            IsLocal = false,
            ExternalProvider = Provider,
            ExternalId = encodedUrl
        };
    }

    private static Album? ParseAlbum(JsonElement item, string? fallbackUrl, string? fallbackArtist)
    {
        var url = GetEntryUrl(item) ?? fallbackUrl;
        var title = GetString(item, "title") ?? GetString(item, "album");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title) || !IsSoundCloudSetUrl(url))
            return null;

        var artist = GetString(item, "album_artist")
            ?? GetString(item, "uploader")
            ?? GetNestedString(item, "user", "username")
            ?? fallbackArtist
            ?? "SoundCloud";
        var profileUrl = GetString(item, "uploader_url")
            ?? GetNestedString(item, "user", "permalink_url")
            ?? ProfileUrlFromSetUrl(url);
        var encodedUrl = EncodeUrl(url);
        var songs = item.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
            ? entries.EnumerateArray().Select(ParseSong).Where(song => song != null).Cast<Song>().ToList()
            : new List<Song>();

        return new Album
        {
            Id = AlbumId(url),
            Title = title,
            Artist = artist,
            ArtistId = IsSoundCloudProfileUrl(profileUrl) ? ArtistId(profileUrl!) : null,
            Year = ReadYear(item),
            SongCount = ReadCount(item, "track_count") ?? ReadCount(item, "playlist_count")
                ?? (songs.Count > 0 ? songs.Count : null),
            ReleaseType = GetString(item, "album_type") ?? GetString(item, "set_type") ?? "album",
            CoverArtUrl = GetThumbnail(item),
            CoverArtUrlLarge = GetThumbnail(item),
            Genre = GetString(item, "genre"),
            IsLocal = false,
            ExternalProvider = Provider,
            ExternalId = encodedUrl,
            Songs = songs
        };
    }

    private static ExternalPlaylist? ParsePlaylist(JsonElement item, string? fallbackUrl, string? fallbackCurator)
    {
        var url = GetEntryUrl(item) ?? fallbackUrl;
        var title = GetString(item, "title");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title) || !IsSoundCloudSetUrl(url))
            return null;

        var encodedUrl = EncodeUrl(url);
        return new ExternalPlaylist
        {
            Id = PlaylistIdHelper.CreatePlaylistId(Provider, encodedUrl),
            Name = title,
            Description = GetString(item, "description"),
            CuratorName = GetString(item, "uploader")
                ?? GetNestedString(item, "user", "username")
                ?? fallbackCurator,
            Provider = Provider,
            ExternalId = encodedUrl,
            TrackCount = ReadCount(item, "playlist_count")
                ?? (item.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
                    ? entries.GetArrayLength() : 0),
            Duration = ReadCount(item, "duration") ?? 0,
            CoverUrl = GetThumbnail(item),
            CreatedDate = ReadDate(item)
        };
    }

    private static bool IsAlbumEntry(JsonElement item)
    {
        var setType = GetString(item, "set_type")
            ?? GetString(item, "album_type")
            ?? GetNestedString(item, "playlist", "set_type");
        return setType != null &&
               (setType.Equals("album", StringComparison.OrdinalIgnoreCase) ||
                setType.Equals("ep", StringComparison.OrdinalIgnoreCase) ||
                setType.Equals("single", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRelevant(string query, string title, string? creator)
    {
        var q = query.Trim();
        if (title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(creator) &&
             creator.Contains(q, StringComparison.OrdinalIgnoreCase)))
            return true;

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 3)
            .ToList();
        return terms.Count > 0 && terms.Any(term =>
            title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(creator) &&
             creator.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? GetEntryUrl(JsonElement item) =>
        GetString(item, "webpage_url")
        ?? GetString(item, "original_url")
        ?? GetString(item, "permalink_url")
        ?? GetString(item, "url");

    private static string? GetSoundCloudPlaylistApiUrl(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("id", out var id))
        {
            var idText = id.ValueKind switch
            {
                JsonValueKind.String => id.GetString(),
                JsonValueKind.Number => id.GetRawText(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(idText) && idText.All(char.IsDigit))
                return $"https://api-v2.soundcloud.com/playlists/{idText}";
        }

        var uri = GetString(item, "uri");
        return IsSoundCloudPlaylistApiUrl(uri) ? uri : null;
    }

    private static string? GetSoundCloudTrackApiUrl(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("id", out var id))
        {
            var idText = id.ValueKind switch
            {
                JsonValueKind.String => id.GetString(),
                JsonValueKind.Number => id.GetRawText(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(idText) && idText.All(char.IsDigit))
                return $"https://api-v2.soundcloud.com/tracks/{idText}";
        }

        var uri = GetString(item, "uri");
        return IsSoundCloudTrackApiUrl(uri) ? uri : null;
    }

    private static bool IsSoundCloudTrackApiUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        var isSoundCloudApi = uri.Host.Equals("api.soundcloud.com", StringComparison.OrdinalIgnoreCase) ||
                              uri.Host.Equals("api-v2.soundcloud.com", StringComparison.OrdinalIgnoreCase);
        return isSoundCloudApi &&
               uri.AbsolutePath.StartsWith("/tracks/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSoundCloudPlaylistApiUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        var isSoundCloudApi = uri.Host.Equals("api.soundcloud.com", StringComparison.OrdinalIgnoreCase) ||
                              uri.Host.Equals("api-v2.soundcloud.com", StringComparison.OrdinalIgnoreCase);
        return isSoundCloudApi &&
               uri.AbsolutePath.StartsWith("/playlists/", StringComparison.OrdinalIgnoreCase);
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

    private static string? GetThumbnail(JsonElement item)
    {
        var thumbnail = GetString(item, "thumbnail") ?? GetString(item, "artwork_url");
        if (!string.IsNullOrWhiteSpace(thumbnail)) return thumbnail;

        if (item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("thumbnails", out var thumbnails) &&
            thumbnails.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in thumbnails.EnumerateArray().Reverse())
            {
                var url = GetString(candidate, "url");
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
        }
        return null;
    }

    private static int? ReadCount(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        try { return (int)Math.Round(value.GetDouble()); }
        catch { return null; }
    }

    private static int? ReadYear(JsonElement item)
    {
        var uploadDate = GetString(item, "upload_date");
        if (uploadDate is { Length: >= 4 } && int.TryParse(uploadDate[..4], out var year))
            return year;

        foreach (var field in new[] { "release_timestamp", "timestamp" })
        {
            if (item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var timestamp))
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(timestamp).Year; } catch { }
            }
        }
        return null;
    }

    private static DateTime? ReadDate(JsonElement item)
    {
        foreach (var field in new[] { "release_timestamp", "timestamp" })
        {
            if (item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var timestamp))
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime; } catch { }
            }
        }

        var uploadDate = GetString(item, "upload_date");
        return DateTime.TryParseExact(uploadDate, "yyyyMMdd", null,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToUniversalTime()
            : null;
    }

    private static string? ProfileUrlFromTrackUrl(string? trackUrl)
    {
        if (!Uri.TryCreate(trackUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("soundcloud.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 ||
            new[] { "search", "discover", "sets", "stations", "tracks", "albums", "playlists" }
                .Contains(segments[0], StringComparer.OrdinalIgnoreCase))
            return null;

        return $"{uri.Scheme}://{uri.Host}/{segments[0]}";
    }

    private static string? ProfileUrlFromSetUrl(string setUrl)
    {
        if (!Uri.TryCreate(setUrl, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || !segments[1].Equals("sets", StringComparison.OrdinalIgnoreCase))
            return null;
        return $"{uri.Scheme}://{uri.Host}/{segments[0]}";
    }

    private static string ArtistId(string url) => $"ext-{Provider}-artist-{EncodeUrl(url)}";
    private static string AlbumId(string url) => $"ext-{Provider}-album-{EncodeUrl(url)}";
    private static string EncodeUrl(string url) => Uri.EscapeDataString(url);
    private static string DecodeUrl(string url) => Uri.UnescapeDataString(url);
    private static bool IsProvider(string? provider) =>
        string.Equals(provider, Provider, StringComparison.OrdinalIgnoreCase);

    private static bool IsSoundCloudUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Host.Equals("soundcloud.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".soundcloud.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSoundCloudProfileUrl(string? value)
    {
        if (!IsSoundCloudUrl(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        var path = uri.AbsolutePath.Trim('/');
        return path.Length > 0 &&
               !path.StartsWith("search/", StringComparison.OrdinalIgnoreCase) &&
               !path.StartsWith("discover/", StringComparison.OrdinalIgnoreCase) &&
               !path.Contains("/sets/", StringComparison.OrdinalIgnoreCase) &&
               !path.Contains("/tracks/", StringComparison.OrdinalIgnoreCase) &&
               !path.Contains("/albums/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSoundCloudSetUrl(string? value)
    {
        if (!IsSoundCloudUrl(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.AbsolutePath.Contains("/sets/", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.Contains("/albums/", StringComparison.OrdinalIgnoreCase) ||
               IsSoundCloudPlaylistApiUrl(value);
    }

    private static string CollectionUrl(string profileUrl, string collection) =>
        $"{profileUrl.TrimEnd('/')}/{collection}";

    private static string ProfileNameFromUrl(string profileUrl)
    {
        if (Uri.TryCreate(profileUrl, UriKind.Absolute, out var uri))
        {
            var slug = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(slug))
                return string.Join(" ", slug.Replace('_', ' ').Replace('-', ' ')
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        }
        return "SoundCloud";
    }

    private static string? StripCollectionSuffix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        foreach (var suffix in new[] { " (All)", " (Tracks)", " (Albums)", " (Sets)" })
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return value[..^suffix.Length];
        }
        return value;
    }

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
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"yt-dlp failed: {error}");
        return output;
    }

    private sealed record Profile(string Url, string Name, string? ImageUrl);
}
