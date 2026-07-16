using System.Diagnostics;
using System.Security.Cryptography;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services;

internal sealed class UpdateInstallerService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private const int DownloadBufferSize = 80 * 1024;

    public async Task<string> DownloadInstallerAsync(
        GitHubUpdateInfo update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.ArtifactUrl))
        {
            throw new InvalidOperationException("更新下载地址为空。");
        }

        var fileName = ResolveInstallerFileName(update);
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前更新产物不是安装包。请前往发布页手动下载。");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "SteamEYA", "updates", update.LatestVersion);
        Directory.CreateDirectory(tempRoot);

        var targetPath = Path.Combine(tempRoot, fileName);
        var downloadPath = targetPath + ".downloading";

        using var response = await HttpClient.GetAsync(
            update.ArtifactUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await CopyWithProgressAsync(source, target, totalBytes, progress, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(update.ArtifactSha256))
        {
            var actualHash = ComputeSha256(downloadPath);
            if (!string.Equals(actualHash, update.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(downloadPath);
                throw new InvalidOperationException("安装包校验失败，请稍后重试。");
            }
        }

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        File.Move(downloadPath, targetPath);
        return targetPath;
    }

    public bool LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath),
            Arguments = "/CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /NORESTARTAPPLICATIONS"
        };

        using var process = Process.Start(startInfo);
        return process is not null;
    }

    public void ForceCloseOtherInstances(string processName, int currentProcessId)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.Id == currentProcessId)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // best-effort：其他实例可能已退出，或权限不足。
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamEYA-Installer");
        return client;
    }

    private static string ResolveInstallerFileName(GitHubUpdateInfo update)
    {
        if (!string.IsNullOrWhiteSpace(update.ArtifactName))
        {
            return Path.GetFileName(update.ArtifactName);
        }

        if (!string.IsNullOrWhiteSpace(update.ArtifactUrl) &&
            Uri.TryCreate(update.ArtifactUrl, UriKind.Absolute, out var uri))
        {
            var fileName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return "SteamEYA-Installer.exe";
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream target,
        long? totalBytes,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[DownloadBufferSize];
        long received = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            progress?.Report(new UpdateDownloadProgress(received, totalBytes));
        }
    }
}

internal sealed record UpdateDownloadProgress(long BytesReceived, long? TotalBytes);
