using System.Diagnostics;
using octo_fiesta.Services.Validation;

namespace octo_fiesta.Services.YouTubeMusic;

/// <summary>
/// Checks that yt-dlp is installed without making a network request at startup.
/// YouTube availability can change by video, location, or sign-in requirements.
/// </summary>
public sealed class YouTubeMusicStartupValidator : IStartupValidator
{
    public string ServiceName => "YouTube Music";

    public async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken)
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

        try
        {
            using var process = Process.Start(start);
            if (process == null)
            {
                WriteStatus("yt-dlp", "UNAVAILABLE", ConsoleColor.Red);
                WriteDetail("Could not start yt-dlp.");
                return ValidationResult.Failure("UNAVAILABLE", "Could not start yt-dlp.");
            }

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                WriteStatus("yt-dlp", "TIMEOUT", ConsoleColor.Yellow);
                WriteDetail("yt-dlp --version exceeded 10 seconds.");
                return ValidationResult.Failure("TIMEOUT", "yt-dlp did not respond within 10 seconds.", ConsoleColor.Yellow);
            }

            var version = (await stdout).Trim();
            var error = (await stderr).Trim();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(version))
            {
                WriteStatus("yt-dlp", "UNAVAILABLE", ConsoleColor.Red);
                WriteDetail(string.IsNullOrWhiteSpace(error) ? "yt-dlp --version failed." : error);
                return ValidationResult.Failure("UNAVAILABLE", error);
            }

            WriteStatus("yt-dlp", version, ConsoleColor.Green);
            WriteDetail("YouTube Music uses yt-dlp and ffmpeg; no API key is needed for public results.");
            return ValidationResult.Success($"yt-dlp {version} is installed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteStatus("yt-dlp", "UNAVAILABLE", ConsoleColor.Red);
            WriteDetail(ex.Message);
            return ValidationResult.Failure("UNAVAILABLE", ex.Message);
        }
    }

    private static void WriteStatus(string label, string value, ConsoleColor color)
    {
        Console.Write($"  {label}: ");
        var original = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(value);
        Console.ForegroundColor = original;
    }

    private static void WriteDetail(string message)
    {
        var original = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"    -> {message}");
        Console.ForegroundColor = original;
    }
}