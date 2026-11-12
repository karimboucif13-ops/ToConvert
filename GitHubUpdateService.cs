using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ToConvert;

internal sealed record GitHubRelease(Version Version, Uri AssetUrl, string Sha256, string? Notes);

internal static class GitHubUpdateService
{
    private const string Repository = "karimboucif13-ops/ToConvert";
    private const long MaximumDownloadSize = 1024L * 1024 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly string UpdatesDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FreeGameX", "Converter", "Updates");
    private static readonly string UpdateErrorPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FreeGameX", "Converter", "update-error.txt");

    public static Version CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);
            return NormalizeVersion(version);
        }
    }

    public static async Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        var requestUri = new Uri($"https://api.github.com/repos/{Repository}/releases/latest");
        using var response = await HttpClient.GetAsync(
            requestUri, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(requestTimeout.Token);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: requestTimeout.Token);
        var root = document.RootElement;
        var tag = GetRequiredString(root, "tag_name");
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsedVersion) || parsedVersion.Build < 0)
        {
            throw new InvalidDataException($"The latest release has an invalid version tag: {tag}");
        }

        var assets = root.GetProperty("assets");
        JsonElement executableAsset = default;
        foreach (var asset in assets.EnumerateArray())
        {
            if (string.Equals(GetRequiredString(asset, "name"), "ToConvert.exe", StringComparison.OrdinalIgnoreCase))
            {
                executableAsset = asset;
                break;
            }
        }

        if (executableAsset.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("The latest GitHub release does not contain ToConvert.exe.");
        }

        var assetUrl = GetRepositoryUri(
            GetRequiredString(executableAsset, "browser_download_url"), "/releases/download/");
        var digest = GetRequiredString(executableAsset, "digest");
        const string sha256Prefix = "sha256:";
        if (!digest.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The release executable does not provide a SHA-256 digest.");
        }

        var sha256 = digest[sha256Prefix.Length..];
        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The release executable has an invalid SHA-256 digest.");
        }

        return new GitHubRelease(
            NormalizeVersion(parsedVersion),
            assetUrl,
            sha256,
            root.TryGetProperty("body", out var body) ? body.GetString() : null);
    }

    public static async Task<string> DownloadAndVerifyAsync(
        GitHubRelease release,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        var downloadPath = Path.Combine(UpdatesDirectory, $"ToConvert-{release.Version}.exe");

        try
        {
            using var response = await HttpClient.GetAsync(
                release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumDownloadSize)
            {
                throw new InvalidDataException("The update executable exceeds the 1 GB download limit.");
            }

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                             downloadPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            await using var downloadedFile = File.OpenRead(downloadPath);
            var actualDigest = await SHA256.HashDataAsync(downloadedFile, cancellationToken);
            var expectedDigest = Convert.FromHexString(release.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            {
                throw new InvalidDataException("The downloaded update failed its SHA-256 verification.");
            }

            return downloadPath;
        }
        catch
        {
            if (File.Exists(downloadPath))
            {
                File.Delete(downloadPath);
            }

            throw;
        }
    }

    public static void EnsureInstallationIsWritable(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("The app's installation folder could not be determined.");
        var probePath = Path.Combine(directory, $".update-check-{Guid.NewGuid():N}");
        using (File.Create(probePath))
        {
        }

        File.Delete(probePath);
    }

    public static void StartUpdater(string downloadPath)
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The running app's executable path could not be determined.");
        EnsureInstallationIsWritable(executablePath);

        Directory.CreateDirectory(Path.GetDirectoryName(UpdateErrorPath)!);
        if (File.Exists(UpdateErrorPath))
        {
            File.Delete(UpdateErrorPath);
        }

        var scriptPath = Path.Combine(UpdatesDirectory, $"apply-update-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, ApplyUpdateScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-ProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-SourcePath");
        startInfo.ArgumentList.Add(downloadPath);
        startInfo.ArgumentList.Add("-TargetPath");
        startInfo.ArgumentList.Add(executablePath);
        startInfo.ArgumentList.Add("-ErrorPath");
        startInfo.ArgumentList.Add(UpdateErrorPath);

        try
        {
            using var updater = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows could not start the update helper.");
        }
        catch
        {
            File.Delete(scriptPath);
            throw;
        }
    }

    public static string? ConsumeUpdateError()
    {
        if (!File.Exists(UpdateErrorPath))
        {
            return null;
        }

        var error = File.ReadAllText(UpdateErrorPath);
        File.Delete(UpdateErrorPath);
        return error;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FreeGameX-Converter");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Uri GetRepositoryUri(string value, string expectedPathPrefix)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith($"/{Repository}{expectedPathPrefix}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The GitHub release contains an unexpected URL.");
        }

        return uri;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"The GitHub release is missing '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private const string ApplyUpdateScript = """
        param(
            [int]$ProcessId,
            [string]$SourcePath,
            [string]$TargetPath,
            [string]$ErrorPath
        )

        $backupPath = "$TargetPath.update-backup"
        $replacementPath = "$TargetPath.update"
        try {
            $app = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
            if ($app) { $app.WaitForExit() }
            Start-Sleep -Milliseconds 300
            Copy-Item -LiteralPath $SourcePath -Destination $replacementPath -Force
            Move-Item -LiteralPath $TargetPath -Destination $backupPath -Force
            Move-Item -LiteralPath $replacementPath -Destination $TargetPath -Force
            Start-Process -FilePath $TargetPath
            Remove-Item -LiteralPath $backupPath -Force
            Remove-Item -LiteralPath $SourcePath -Force
        }
        catch {
            $failure = $_.Exception.ToString()
            try {
                if (Test-Path -LiteralPath $backupPath) {
                    if (Test-Path -LiteralPath $TargetPath) {
                        Remove-Item -LiteralPath $TargetPath -Force
                    }
                    Move-Item -LiteralPath $backupPath -Destination $TargetPath -Force
                }
                if (Test-Path -LiteralPath $TargetPath) {
                    Start-Process -FilePath $TargetPath
                }
            }
            catch {
                $failure += [Environment]::NewLine + $_.Exception.ToString()
            }
            [IO.File]::WriteAllText($ErrorPath, $failure)
        }
        finally {
            Remove-Item -LiteralPath $replacementPath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        }
        """;
}
