using System.IO.Compression;
using System.Security.Cryptography;

namespace AgentOrb.Setup;

internal sealed record AnodeProvisionResult(
    string ExecutablePath,
    bool InstalledBySetup,
    string ArchiveSha256);

internal static class AnodeProvisioner
{
    public const string SupportedVersion = "0.11.4";
    public const string DownloadUrl =
        "https://github.com/skulitom/Anode/releases/download/v0.11.4/anode-windows-x64.zip";
    public const string PinnedSha256 = "c5dea4866079a635240e40d9bdeda0bb95ace537b90edebf6835c668b0e8d711";

    private const long MaxArchiveBytes = 300L * 1024 * 1024;
    private const long MaxExtractedBytes = 600L * 1024 * 1024;
    private static readonly HttpClient Http = CreateHttpClient();

    public static async Task<AnodeProvisionResult> EnsureAsync(
        SetupOptions options,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        var existing = SetupInspector.FindAnodeExecutable();
        if (existing is not null)
        {
            report($"使用现有 Anode：{existing}");
            return new AnodeProvisionResult(existing, InstalledBySetup: false, ArchiveSha256: string.Empty);
        }

        var expectedHash = string.IsNullOrWhiteSpace(options.AnodeSha256)
            ? PinnedSha256
            : options.AnodeSha256;
        var archive = ResolveArchive(options);
        var temporaryArchive = false;
        if (archive is null || !File.Exists(archive))
        {
            archive = Path.Combine(Path.GetTempPath(), $"agentorb-anode-{Guid.NewGuid():N}.zip");
            temporaryArchive = true;
            report($"下载 Anode {SupportedVersion}：{DownloadUrl}");
            await DownloadAsync(archive, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            report($"校验本地 Anode 包：{archive}");
        }

        try
        {
            var actualHash = await ComputeSha256Async(archive, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Anode 安装包 SHA-256 不匹配，已拒绝安装。请使用官方 v0.11.4 x64 包，或显式传入可信校验和。");
            }

            report("Anode 安装包校验通过。");
            var executable = ExtractAndInstall(archive);
            return new AnodeProvisionResult(executable, InstalledBySetup: true, actualHash);
        }
        finally
        {
            if (temporaryArchive)
            {
                TryDelete(archive);
            }
        }
    }

    private static string? ResolveArchive(SetupOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.AnodeArchive))
        {
            var explicitPath = Path.GetFullPath(options.AnodeArchive);
            if (!File.Exists(explicitPath))
            {
                throw new FileNotFoundException("指定的 Anode 安装包不存在。", explicitPath);
            }

            return explicitPath;
        }

        return File.Exists(SetupPaths.DefaultAnodeArchive)
            ? SetupPaths.DefaultAnodeArchive
            : null;
    }

    private static async Task DownloadAsync(string destination, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(
            DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxArchiveBytes)
        {
            throw new InvalidDataException("Anode 安装包体积异常，已拒绝下载。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxArchiveBytes)
            {
                throw new InvalidDataException("Anode 安装包超过允许大小，已中止下载。");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > MaxArchiveBytes)
        {
            throw new InvalidDataException("Anode 安装包不存在或体积异常。");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ExtractAndInstall(string archivePath)
    {
        var staging = Path.Combine(Path.GetTempPath(), $"agentorb-anode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > 5000)
            {
                throw new InvalidDataException("Anode 安装包包含过多文件，已拒绝解压。");
            }

            long extractedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                extractedBytes += entry.Length;
                if (extractedBytes > MaxExtractedBytes)
                {
                    throw new InvalidDataException("Anode 安装包解压体积异常，已拒绝安装。");
                }

                var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!SetupPaths.IsWithin(destination, staging))
                {
                    throw new InvalidDataException("Anode 安装包包含非法路径，已拒绝安装。");
                }
            }

            ZipFile.ExtractToDirectory(archivePath, staging, overwriteFiles: true);
            var sourceExecutable = Directory
                .EnumerateFiles(staging, "anode.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (sourceExecutable is null)
            {
                throw new InvalidDataException("Anode 安装包中未找到 anode.exe。");
            }

            var sourceRoot = Path.GetDirectoryName(sourceExecutable)!;
            Directory.CreateDirectory(SetupPaths.DefaultAnodeRoot);
            CopyDirectory(sourceRoot, SetupPaths.DefaultAnodeRoot);
            var installedExecutable = Path.Combine(SetupPaths.DefaultAnodeRoot, "anode.exe");
            if (!File.Exists(installedExecutable))
            {
                throw new IOException("Anode 安装失败：目标目录中缺少 anode.exe。");
            }

            return installedExecutable;
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (IOException)
            {
                // Temporary extraction cleanup must not hide the install result.
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AgentOrb-Setup/0.1.10");
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
