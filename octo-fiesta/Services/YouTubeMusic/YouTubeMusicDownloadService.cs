using System.Diagnostics;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Services.YouTubeMusic;

/// <summary>
/// Downloads publicly accessible YouTube Music tracks through yt-dlp.
/// Videos that require authentication or are unavailable to yt-dlp cannot be downloaded.
/// </summary>
public sealed class YouTubeMusicDownloadService : BaseDownloadService
{
    protected override string ProviderName => "youtube";

    public YouTubeMusicDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IServiceProvider serviceProvider,
        ILogger<YouTubeMusicDownloadService> logger)
        : base(httpClientFactory, configuration, localLibraryService, metadataService,
            subsonicSettings.Value, serviceProvider, logger)
    {
    }

    public override Task<bool> IsAvailableAsync()
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--version");
            using var process = Process.Start(start);
            if (process == null) return Task.FromResult(false);
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return Task.FromResult(false);
            }
            return Task.FromResult(process.ExitCode == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId)
    {
        const string prefix = "ext-youtube-album-";
        return albumId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               albumId.Length > prefix.Length
            ? albumId[prefix.Length..]
            : null;
    }

    protected override string? GetTargetQuality() => "MP3_320";

    protected override async Task<DownloadResult> DownloadTrackAsync(
        string trackId, Song song, CancellationToken cancellationToken)
    {
        var url = GetTrackUrl(trackId);
        if (url == null)
            throw new InvalidOperationException("Invalid YouTube Music track ID or URL.");

        var tempDir = Path.Combine(Path.GetTempPath(), "octo-fiesta-youtube", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var outputTemplate = Path.Combine(tempDir, "%(id)s.%(ext)s");
            var start = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in new[]
            {
                "--no-playlist", "--no-warnings", "--extract-audio", "--audio-format", "mp3",
                "--audio-quality", "0", "--output", outputTemplate, url
            })
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start yt-dlp.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("yt-dlp did not finish the YouTube Music download within 5 minutes.");
            }

            var stderr = await stderrTask;
            _ = await stdoutTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"yt-dlp could not download this YouTube track: {stderr}");

            var file = Directory.GetFiles(tempDir, "*.mp3").FirstOrDefault();
            if (file == null)
                throw new InvalidOperationException("yt-dlp completed without producing an MP3 file.");

            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return new DownloadResult(stream, ".mp3", "MP3_320");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static string? GetTrackUrl(string externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;

        var value = Uri.UnescapeDataString(externalId);
        if (System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_-]{11}$"))
            return $"https://music.youtube.com/watch?v={Uri.EscapeDataString(value)}";

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;

        var isYouTubeHost = uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
                            uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                            uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
        if (!isYouTubeHost)
            return null;

        if (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
        {
            var videoId = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(parts => parts.Length == 2 &&
                                Uri.UnescapeDataString(parts[0]).Equals("v", StringComparison.OrdinalIgnoreCase))
                .Select(parts => Uri.UnescapeDataString(parts[1]))
                .FirstOrDefault();

            if (videoId != null && System.Text.RegularExpressions.Regex.IsMatch(videoId, "^[A-Za-z0-9_-]{11}$"))
                return $"https://music.youtube.com/watch?v={Uri.EscapeDataString(videoId)}";
        }

        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var videoId = uri.AbsolutePath.Trim('/');
            if (System.Text.RegularExpressions.Regex.IsMatch(videoId, "^[A-Za-z0-9_-]{11}$"))
                return $"https://music.youtube.com/watch?v={Uri.EscapeDataString(videoId)}";
        }

        return null;
    }
}