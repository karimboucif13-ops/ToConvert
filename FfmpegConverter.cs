using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace ToConvert;

internal static class FfmpegConverter
{
    private static readonly string[] ExecutableCandidates =
    [
        Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
        Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "ffmpeg.exe")
    ];
    private static readonly Lazy<string?> BundledExecutablePath = new(ExtractBundledFiles);
    private const string BundledResourcePrefix = "ToConvert.Ffmpeg.";

    internal static readonly FormatOption[] AudioFormats =
    [
        new("MP3 Audio (.mp3)", ".mp3"),
        new("WAV Audio (.wav)", ".wav"),
        new("AAC Audio (.aac)", ".aac"),
        new("M4A Audio (.m4a)", ".m4a"),
        new("FLAC Audio (.flac)", ".flac"),
        new("OGG Audio (.ogg)", ".ogg"),
        new("Opus Audio (.opus)", ".opus"),
        new("WMA Audio (.wma)", ".wma"),
        new("AIFF Audio (.aiff)", ".aiff"),
        new("AC-3 Audio (.ac3)", ".ac3"),
        new("MP2 Audio (.mp2)", ".mp2"),
        new("CAF Audio (.caf)", ".caf"),
        new("AU Audio (.au)", ".au"),
        new("Matroska Audio (.mka)", ".mka")
    ];

    internal static readonly FormatOption[] VideoFormats =
    [
        new("MP4 Video (.mp4)", ".mp4"),
        new("MKV Video (.mkv)", ".mkv"),
        new("MOV Video (.mov)", ".mov"),
        new("WebM Video (.webm)", ".webm"),
        new("AVI Video (.avi)", ".avi"),
        new("WMV Video (.wmv)", ".wmv"),
        new("FLV Video (.flv)", ".flv"),
        new("MPEG Video (.mpeg)", ".mpeg"),
        new("MPEG Video (.mpg)", ".mpg"),
        new("M4V Video (.m4v)", ".m4v"),
        new("MPEG-TS Video (.ts)", ".ts"),
        new("OGV Video (.ogv)", ".ogv"),
        new("3GP Video (.3gp)", ".3gp"),
        new("VOB Video (.vob)", ".vob"),
        new("ASF Video (.asf)", ".asf"),
        new("MXF Video (.mxf)", ".mxf")
    ];

    internal static readonly FormatOption[] ExtendedImageFormats =
    [
        new("ICO Image (.ico)", ".ico"),
        new("WebP Image (.webp)", ".webp"),
        new("AVIF Image (.avif)", ".avif"),
        new("JPEG 2000 Image (.jp2)", ".jp2"),
        new("TGA Image (.tga)", ".tga"),
        new("PCX Image (.pcx)", ".pcx"),
        new("DDS Image (.dds)", ".dds"),
        new("PPM Image (.ppm)", ".ppm"),
        new("PGM Image (.pgm)", ".pgm"),
        new("PBM Image (.pbm)", ".pbm"),
        new("PAM Image (.pam)", ".pam")
    ];

    private static readonly HashSet<string> AudioExtensions =
        new(AudioFormats.Select(format => format.Extension), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> VideoExtensions = new(
        VideoFormats.Select(format => format.Extension).Concat([".mp4", ".mkv", ".mov", ".webm", ".avi", ".wmv", ".flv", ".mpeg", ".mpg", ".m4v", ".ts", ".ogv", ".3gp", ".vob", ".asf", ".mxf"]),
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ImageExtensions = new(
        ExtendedImageFormats.Select(format => format.Extension).Concat(
            [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"]),
        StringComparer.OrdinalIgnoreCase);

    internal static bool IsAvailable => TryFindExecutable() is not null;

    internal static bool IsAudioExtension(string extension) => AudioExtensions.Contains(extension);

    internal static bool IsVideoExtension(string extension) => VideoExtensions.Contains(extension);

    internal static bool IsExtendedImageExtension(string extension) => ImageExtensions.Contains(extension);

    internal static bool IsImageExtension(string extension) => ImageExtensions.Contains(extension);

    internal static bool CanConvert(string sourceExtension, string targetExtension) =>
        IsAvailable && ((IsAudioExtension(sourceExtension) && IsAudioExtension(targetExtension)) ||
                        (IsVideoExtension(sourceExtension) &&
                         (IsAudioExtension(targetExtension) || IsVideoExtension(targetExtension))) ||
                        (IsImageExtension(sourceExtension) && IsImageExtension(targetExtension)));

    internal static void Convert(string sourcePath, string destinationPath, string targetExtension)
    {
        var executable = TryFindExecutable()
            ?? throw new FileNotFoundException(
                "The FFmpeg engine could not be found. Reinstall the app to restore its bundled tools.");

        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", sourcePath
        };

        if (IsAudioExtension(targetExtension))
        {
            arguments.AddRange(["-map", "0:a:0", "-vn", "-sn", "-dn"]);
            arguments.AddRange(GetAudioCodecArguments(targetExtension));
        }
        else if (IsVideoExtension(targetExtension))
        {
            arguments.AddRange(["-map", "0:v:0", "-map", "0:a?", "-sn", "-dn"]);
            arguments.AddRange(GetVideoCodecArguments(targetExtension));
        }
        else if (IsExtendedImageExtension(targetExtension))
        {
            arguments.AddRange(["-map", "0:v:0", "-frames:v", "1"]);
            arguments.AddRange(targetExtension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
                ? ["-c:v", "png"]
                : GetImageCodecArguments(targetExtension));
        }
        else
        {
            throw new NotSupportedException($"The output format {targetExtension} is not supported.");
        }

        var iconOutputPath = targetExtension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetTempPath(), $"FreeGameX-{Guid.NewGuid():N}.png")
            : null;
        arguments.Add(iconOutputPath ?? destinationPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the FFmpeg engine.");
            var errorTask = process.StandardError.ReadToEndAsync();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();
            var error = errorTask.GetAwaiter().GetResult();
            _ = outputTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                var detail = error.Trim();
                if (detail.Length > 1200)
                {
                    detail = detail[^1200..];
                }

                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(detail)
                        ? $"FFmpeg conversion failed (exit code {process.ExitCode})."
                        : $"FFmpeg conversion failed:\n{detail}");
            }

            if (iconOutputPath is not null)
            {
                FileConverter.ConvertPngToIcon(iconOutputPath, destinationPath);
            }
        }
        finally
        {
            if (iconOutputPath is not null && File.Exists(iconOutputPath))
            {
                File.Delete(iconOutputPath);
            }
        }
    }

    private static string? TryFindExecutable()
    {
        foreach (var candidate in ExecutableCandidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (BundledExecutablePath.Value is { } bundledPath)
        {
            return bundledPath;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? ExtractBundledFiles()
    {
        var resources = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(name => name.StartsWith(BundledResourcePrefix, StringComparison.Ordinal))
            .ToArray();
        if (resources.Length == 0)
        {
            return null;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FreeGameX", "Converter", "ffmpeg");
        Directory.CreateDirectory(directory);

        foreach (var resourceName in resources)
        {
            var fileName = resourceName[BundledResourcePrefix.Length..];
            var destinationPath = Path.Combine(directory, fileName);
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"Bundled FFmpeg file '{fileName}' is missing.");

            if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length == resource.Length)
            {
                continue;
            }

            var temporaryPath = destinationPath + ".tmp";
            using (var output = File.Create(temporaryPath))
            {
                resource.CopyTo(output);
            }
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }

        return Path.Combine(directory, "ffmpeg.exe");
    }

    private static IEnumerable<string> GetAudioCodecArguments(string extension) => extension switch
    {
        ".mp3" => ["-c:a", "libmp3lame", "-q:a", "2"],
        ".wav" => ["-c:a", "pcm_s16le"],
        ".aac" or ".m4a" => ["-c:a", "aac", "-b:a", "192k"],
        ".flac" => ["-c:a", "flac"],
        ".ogg" => ["-c:a", "libvorbis", "-q:a", "4"],
        ".opus" => ["-c:a", "libopus", "-b:a", "128k"],
        ".wma" => ["-c:a", "wmav2", "-b:a", "192k"],
        ".aiff" => ["-c:a", "pcm_s16be"],
        ".ac3" => ["-c:a", "ac3", "-b:a", "192k"],
        ".mp2" => ["-c:a", "mp2", "-b:a", "192k"],
        ".caf" => ["-c:a", "pcm_s16le"],
        ".au" => ["-c:a", "pcm_s16be"],
        ".mka" => ["-c:a", "flac"],
        _ => throw new NotSupportedException($"The audio format {extension} is not supported.")
    };

    private static IEnumerable<string> GetVideoCodecArguments(string extension) => extension switch
    {
        ".webm" => ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-c:a", "libopus", "-b:a", "128k"],
        ".ogv" => ["-c:v", "libtheora", "-q:v", "7", "-c:a", "libvorbis", "-q:a", "4"],
        ".wmv" or ".asf" => ["-c:v", "wmv2", "-b:v", "2M", "-c:a", "wmav2", "-b:a", "192k"],
        ".flv" => ["-c:v", "flv", "-b:v", "2M", "-c:a", "libmp3lame", "-b:a", "128k"],
        ".avi" => ["-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame", "-b:a", "192k"],
        ".mpeg" or ".mpg" or ".vob" or ".ts" =>
            ["-c:v", "mpeg2video", "-q:v", "4", "-c:a", "mp2", "-b:a", "192k"],
        ".mxf" => ["-c:v", "mpeg2video", "-q:v", "4", "-c:a", "pcm_s16le"],
        ".3gp" => ["-c:v", "mpeg4", "-b:v", "1M", "-c:a", "aac", "-b:a", "96k"],
        _ => ["-c:v", "libopenh264", "-b:v", "2M", "-c:a", "aac", "-b:a", "192k"]
    };

    private static IEnumerable<string> GetImageCodecArguments(string extension) => extension switch
    {
        ".png" => ["-c:v", "png"],
        ".jpg" or ".jpeg" => ["-c:v", "mjpeg", "-q:v", "2"],
        ".bmp" => ["-c:v", "bmp"],
        ".gif" => ["-c:v", "gif"],
        ".tif" or ".tiff" => ["-c:v", "tiff"],
        ".webp" => ["-c:v", "libwebp", "-quality", "90"],
        ".avif" => ["-c:v", "libaom-av1", "-still-picture", "1", "-crf", "25", "-cpu-used", "6"],
        ".jp2" => ["-c:v", "jpeg2000"],
        ".tga" => ["-c:v", "targa"],
        ".pcx" => ["-c:v", "pcx"],
        ".dds" => ["-c:v", "dds"],
        ".ppm" => ["-c:v", "ppm"],
        ".pgm" => ["-c:v", "pgm"],
        ".pbm" => ["-c:v", "pbm"],
        ".pam" => ["-c:v", "pam"],
        _ => throw new NotSupportedException($"The image format {extension} is not supported.")
    };
}

internal sealed record FormatOption(string Label, string Extension);
