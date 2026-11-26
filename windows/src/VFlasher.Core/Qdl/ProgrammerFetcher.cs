using System.Formats.Tar;
using System.Security.Cryptography;
using ZstdSharp;

namespace VFlasher.Core.Qdl;

/// <summary>
/// Gets Qualcomm's firehose programmer for a board. It is taken from the board
/// vendor's image archive: the archive is streamed only until the programmer
/// has been read (a few MB of a multi-GB download), and the file is checked
/// against the SHA-256 pinned in the manifest.
/// </summary>
public static class ProgrammerFetcher
{
    public static async Task<string> GetAsync(HttpClient http, ImageManifest m, string cacheDir,
        CancellationToken ct = default)
    {
        var url = m.Get("QDL_PROGRAMMER_URL") ?? throw new FormatException("missing QDL_PROGRAMMER_URL");
        var sha = m.Get("QDL_PROGRAMMER_SHA256") ?? throw new FormatException("missing QDL_PROGRAMMER_SHA256");
        var format = m.Get("QDL_PROGRAMMER_FORMAT") ?? "tar.zst";

        Directory.CreateDirectory(cacheDir);
        var file = Path.Combine(cacheDir, $"firehose-{sha[..16]}.elf");
        if (File.Exists(file) && await ImageDownloader.HashMatchesAsync(file, sha, null, ct))
            return file;

        byte[] data;
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            data = format switch
            {
                "elf" => await ReadAllAsync(body, ct),
                "tar.zst" => await ExtractFromTarZstAsync(body,
                    m.Get("QDL_PROGRAMMER_PATH") ?? throw new FormatException("missing QDL_PROGRAMMER_PATH"), ct),
                _ => throw new FormatException($"unsupported QDL_PROGRAMMER_FORMAT '{format}'"),
            };
        }

        if (Convert.ToHexStringLower(SHA256.HashData(data)) != sha)
            throw new InvalidDataException("could not get a programmer with the expected checksum");
        await File.WriteAllBytesAsync(file, data, ct);
        return file;
    }

    static async Task<byte[]> ReadAllAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    static string Normalize(string name) => name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name;

    /// <summary>Reads entries until <paramref name="member"/> and returns its contents.</summary>
    public static async Task<byte[]> ExtractFromTarZstAsync(Stream archive, string member, CancellationToken ct)
    {
        var wanted = Normalize(member);
        await using var zst = new DecompressionStream(archive, leaveOpen: true);
        await using var tar = new TarReader(zst, leaveOpen: true);
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            if (Normalize(entry.Name) != wanted || entry.DataStream is null)
                continue;
            return await ReadAllAsync(entry.DataStream, ct);
        }
        throw new InvalidDataException($"'{member}' was not found in the archive");
    }
}
