using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace VFlasher.Core;

public readonly record struct TransferProgress(string Stage, long Done, long? Total);

/// <summary>
/// Downloads an image into a cache folder (resuming partial downloads) and checks
/// its SHA-256. A file that fails the check is never returned.
/// </summary>
public sealed class ImageDownloader(HttpClient http, string cacheDir)
{
    public string CacheDir { get; } = cacheDir;

    public static string DefaultCacheDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VFlasher", "cache");

    public async Task<string> GetImageAsync(ImageManifest manifest, IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(CacheDir);
        var file = Path.Combine(CacheDir, manifest.ImageFile);
        var part = file + ".part";

        if (File.Exists(file))
        {
            if (await HashMatchesAsync(file, manifest.Sha256, progress, ct))
                return file;
            File.Delete(file);
        }

        await DownloadAsync(manifest, part, progress, ct);

        if (!await HashMatchesAsync(part, manifest.Sha256, progress, ct))
        {
            File.Delete(part);
            throw new InvalidDataException(
                $"Checksum mismatch for {manifest.ImageFile}. The download was deleted; nothing was flashed.");
        }
        File.Move(part, file, overwrite: true);
        return file;
    }

    async Task DownloadAsync(ImageManifest manifest, string part, IProgress<TransferProgress>? progress,
        CancellationToken ct)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(manifest, part, progress, ct);
                return;
            }
            catch (Exception e) when (attempt < attempts && e is HttpRequestException or IOException
                                          && !ct.IsCancellationRequested)
            {
                // Resume from where the connection dropped.
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    async Task DownloadOnceAsync(ImageManifest manifest, string part, IProgress<TransferProgress>? progress,
        CancellationToken ct)
    {
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, manifest.ImageUrl);
        if (existing > 0)
            req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Already complete (or larger than the server's copy); let the hash decide.
            return;
        }
        resp.EnsureSuccessStatusCode();

        var resumed = resp.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
            existing = 0;
        long? total = resp.Content.Headers.ContentLength is { } len ? len + existing : manifest.ImageSize;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write,
            FileShare.None, 1 << 20, useAsync: true);

        var buffer = new byte[1 << 20];
        var done = existing;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            progress?.Report(new TransferProgress("Downloading", done, total));
        }
    }

    public static async Task<bool> HashMatchesAsync(string path, string expectedSha256,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            useAsync: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long done = 0, total = fs.Length;
        int n;
        while ((n = await fs.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, n);
            done += n;
            progress?.Report(new TransferProgress("Verifying", done, total));
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset()) == expectedSha256;
    }
}
