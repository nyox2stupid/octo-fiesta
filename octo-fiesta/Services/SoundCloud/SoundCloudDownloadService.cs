using System.Diagnostics;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;

namespace octo_fiesta.Services.SoundCloud;

/// <summary>
/// Downloads SoundCloud tracks through yt-dlp. Only tracks the uploader makes downloadable
/// through SoundCloud/yt-dlp are supported; no login or DRM bypass is attempted.
/// </summary>
public sealed class SoundCloudDownloadService : BaseDownloadService
{
    protected override string ProviderName => "soundcloud";

    public SoundCloudDownloadService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalLibraryService localLibraryService,
        IMusicMetadataService metadataService,
        IOptions<SubsonicSettings> subsonicSettings,
        IServiceProvider serviceProvider,
        ILogger<SoundCloudDownloadService> logger)
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
            process.WaitForExit(5000);
            return Task.FromResult(process.ExitCode == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    protected override string? ExtractExternalIdFromAlbumId(string albumId) => null;

    protected override string? GetTargetQuality() => "MP3_320";

    protected override async Task<DownloadResult> DownloadTrackAsync(
        string trackId, Song song, CancellationToken cancellationToken)
    {
        var url = Uri.UnescapeDataString(trackId);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("soundcloud.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid SoundCloud track URL.");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "octo-fiesta-soundcloud", Guid.NewGuid().ToString("N"));
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
            }) start.ArgumentList.Add(arg);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start yt-dlp.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;
            _ = await stdoutTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"yt-dlp could not download this SoundCloud track: {stderr}");

            var file = Directory.GetFiles(tempDir, "*.mp3").FirstOrDefault();
            if (file == null)
                throw new InvalidOperationException("yt-dlp completed without producing an MP3 file.");

            // BaseDownloadService consumes this stream and writes it into the configured library.
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return new DownloadResult(stream, ".mp3", "MP3_320");
        }
        finally
        {
            // The stream is opened before this cleanup; DeleteOnClose keeps the file available
            // until BaseDownloadService has copied it.
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
