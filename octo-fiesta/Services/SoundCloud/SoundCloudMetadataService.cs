using System.Diagnostics;
using System.Text.Json;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;

namespace octo_fiesta.Services.SoundCloud;

/// <summary>
/// SoundCloud search and metadata through yt-dlp's supported SoundCloud extractor.
/// </summary>
public sealed class SoundCloudMetadataService : IMusicMetadataService
{
    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        try
        {
            var json = await RunYtDlpAsync(new[]
            {
                "--ignore-errors", "--no-warnings", "--flat-playlist",
                "--dump-single-json", "--skip-download", $"scsearch{Math.Clamp(limit, 1, 50)}:{query}"
            });
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var entries)) return new();
            return entries.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object)
                .Select(ParseSong)
                .Where(s => s != null)
                .Cast<Song>()
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
        => new() { Songs = await SearchSongsAsync(query, songLimit) };

    public Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20) => Task.FromResult(new List<Album>());
    public Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20) => Task.FromResult(new List<Artist>());
    public Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId) => Task.FromResult(new List<Album>());
    public Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20) => Task.FromResult(new List<ExternalPlaylist>());
    public Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId) => Task.FromResult<ExternalPlaylist?>(null);
    public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId) => Task.FromResult(new List<Song>());
    public Task<Album?> GetAlbumAsync(string externalProvider, string externalId) => Task.FromResult<Album?>(null);
    public Task<Artist?> GetArtistAsync(string externalProvider, string externalId) => Task.FromResult<Artist?>(null);

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (!string.Equals(externalProvider, "soundcloud", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var url = Uri.UnescapeDataString(externalId);
            var json = await RunYtDlpAsync(new[] { "--no-warnings", "--dump-single-json", "--skip-download", url });
            using var doc = JsonDocument.Parse(json);
            return ParseSong(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static Song? ParseSong(JsonElement item)
    {
        var url = GetString(item, "webpage_url") ?? GetString(item, "original_url") ?? GetString(item, "url");
        var title = GetString(item, "track") ?? GetString(item, "title");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("soundcloud.com", StringComparison.OrdinalIgnoreCase)) return null;

        var artist = GetString(item, "artist") ?? GetString(item, "uploader") ?? "Unknown Artist";
        var id = Uri.EscapeDataString(url);
        var uploadDate = GetString(item, "upload_date");
        int? year = uploadDate?.Length >= 4 && int.TryParse(uploadDate[..4], out var y) ? y : null;
        int? duration = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(d.GetDouble()) : null;
        var thumbnail = GetString(item, "thumbnail");

        return new Song
        {
            Id = $"ext-soundcloud-song-{id}",
            Title = title,
            Artist = artist,
            Album = GetString(item, "album") ?? "SoundCloud",
            Duration = duration,
            Year = year,
            CoverArtUrl = thumbnail,
            CoverArtUrlLarge = thumbnail,
            IsLocal = false,
            ExternalProvider = "soundcloud",
            ExternalId = id
        };
    }

    private static string? GetString(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

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
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start yt-dlp.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"yt-dlp failed: {error}");
        return output;
    }
}
