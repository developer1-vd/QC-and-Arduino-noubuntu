using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using VFlasher.Core;

namespace VFlasher.Core.Tests;

public class ManifestTests
{
    const string Valid = """
        # comment
        NAME=Test image
        IMAGE_URL=https://example.org/a.raw.xz
        IMAGE_FILE=a.raw.xz
        IMAGE_SHA256=91a6be7271da83fa77670002b6f275dacfb12d845ae57025d07d4a647cf56a9b
        IMAGE_FORMAT=raw.xz
        FLASH_METHOD=dd
        IMAGE_SIZE=65972
        """;

    [Fact]
    public void ParsesValidManifest()
    {
        var m = ImageManifest.Parse("qualcomm", "board", "fedora", Valid);
        Assert.Equal("Test image", m.Name);
        Assert.Equal(65972, m.ImageSize);
        Assert.Equal("qualcomm/board/fedora", m.Id);
    }

    [Theory]
    [InlineData("IMAGE_FILE=a.raw.xz", "IMAGE_FILE=../../evil")]
    [InlineData("IMAGE_SHA256=91a6", "IMAGE_SHA256=XYZ")]
    [InlineData("IMAGE_URL=https://example.org/a.raw.xz", "IMAGE_URL=file:///etc/passwd")]
    [InlineData("NAME=Test image", "name=lowercase")]
    [InlineData("FLASH_METHOD=dd", "")]
    public void RejectsBadManifest(string find, string replace)
    {
        var text = string.Join('\n', Valid.Split('\n').Select(l => l.StartsWith(find) ? replace : l));
        Assert.Throws<FormatException>(() => ImageManifest.Parse("qualcomm", "board", "fedora", text));
    }

    [Theory]
    [InlineData("Qualcomm")]
    [InlineData("../x")]
    [InlineData("")]
    public void RejectsBadNames(string name) =>
        Assert.Throws<FormatException>(() => ImageManifest.Parse(name, "board", "fedora", Valid));

    [Fact]
    public void RepositoryImagesMatchIndex()
    {
        var dir = ManifestCatalog.FindLocalImagesDir(AppContext.BaseDirectory);
        Assert.NotNull(dir);
        var local = ManifestCatalog.LoadLocal(dir!).Select(m => m.Id).ToList();
        var index = ManifestCatalog.ParseIndex(File.ReadAllText(Path.Combine(dir!, ManifestCatalog.IndexFile)));
        Assert.NotEmpty(local);
        Assert.Equal(local.Order(), index.Order());
    }
}

public class DownloadTests : IDisposable
{
    static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "testdata", "sample.raw.xz");
    const string SampleSha = "91a6be7271da83fa77670002b6f275dacfb12d845ae57025d07d4a647cf56a9b";
    const string RawSha = "999f7ef3069efd89039a695c22da0660d186fe10d9708389173308b059d731c9";

    readonly string cache = Path.Combine(Path.GetTempPath(), "vflasher-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(cache)) Directory.Delete(cache, true);
    }

    static ImageManifest Manifest(string sha) => ImageManifest.Parse("t", "b", "o", $"""
        NAME=Sample
        IMAGE_URL=https://example.org/sample.raw.xz
        IMAGE_FILE=sample.raw.xz
        IMAGE_SHA256={sha}
        IMAGE_FORMAT=raw.xz
        FLASH_METHOD=dd
        """);

    [Fact]
    public async Task DownloadsResumesAndVerifies()
    {
        // The server drops the connection after 20 KB on the first request.
        var handler = new FlakyRangeServer(File.ReadAllBytes(Sample), failAfter: 20_000);
        var dl = new ImageDownloader(new HttpClient(handler), cache);

        var path = await dl.GetImageAsync(Manifest(SampleSha));

        Assert.Equal(SampleSha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Contains(handler.Requests, r => r is { From: > 0 });
        Assert.False(File.Exists(path + ".part"));

        // Second call uses the cache without downloading again.
        var before = handler.Requests.Count;
        await dl.GetImageAsync(Manifest(SampleSha));
        Assert.Equal(before, handler.Requests.Count);
    }

    [Fact]
    public async Task RejectsChecksumMismatch()
    {
        var dl = new ImageDownloader(new HttpClient(new FlakyRangeServer(File.ReadAllBytes(Sample), int.MaxValue)), cache);
        await Assert.ThrowsAsync<InvalidDataException>(() => dl.GetImageAsync(Manifest(new string('0', 64))));
        Assert.Empty(Directory.GetFiles(cache));
    }

    [Fact]
    public void DecompressesXz()
    {
        using var img = new ImageDecompressor(Sample, "raw.xz");
        using var ms = new MemoryStream();
        img.Raw.CopyTo(ms);
        Assert.Equal(RawSha, Convert.ToHexStringLower(SHA256.HashData(ms.ToArray())));
        Assert.Equal(img.CompressedLength, img.CompressedPosition);
    }

    [Theory]
    [InlineData("raw.gz")]
    [InlineData("raw.zst")]
    [InlineData("raw")]
    public void DecompressesOtherFormats(string format)
    {
        byte[] raw;
        using (var xz = new ImageDecompressor(Sample, "raw.xz"))
        using (var ms = new MemoryStream())
        {
            xz.Raw.CopyTo(ms);
            raw = ms.ToArray();
        }

        Directory.CreateDirectory(cache);
        var file = Path.Combine(cache, "img." + format);
        using (var fs = File.Create(file))
        using (Stream w = format switch
               {
                   "raw.gz" => new GZipStream(fs, CompressionLevel.Fastest),
                   "raw.zst" => new ZstdSharp.CompressionStream(fs),
                   _ => fs,
               })
            w.Write(raw);

        using var img = new ImageDecompressor(file, format);
        var buf = new byte[raw.Length + 10];
        var n = img.ReadFull(buf);
        Assert.Equal(raw.Length, n);
        Assert.Equal(raw, buf[..n]);
    }

    /// <summary>Serves a byte array with Range support and can cut the first response short.</summary>
    sealed class FlakyRangeServer(byte[] data, int failAfter) : HttpMessageHandler
    {
        public List<(long From, long Length)> Requests { get; } = [];
        bool failed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            long from = request.Headers.Range?.Ranges.First().From ?? 0;
            var body = data.AsMemory((int)from).ToArray();
            Requests.Add((from, body.Length));
            Stream stream = new MemoryStream(body);
            if (!failed && body.Length > failAfter)
            {
                failed = true;
                stream = new BreakingStream(body, failAfter);
            }
            var resp = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            resp.Content.Headers.ContentLength = body.Length;
            if (from > 0)
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length);
            return Task.FromResult(resp);
        }
    }

    sealed class BreakingStream(byte[] data, int breakAt) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= breakAt) throw new IOException("connection reset");
            return base.Read(buffer, offset, (int)Math.Min(count, breakAt - Position));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var tmp = new byte[buffer.Length];
            var n = Read(tmp, 0, tmp.Length);
            tmp.AsSpan(0, n).CopyTo(buffer.Span);
            return ValueTask.FromResult(n);
        }
    }
}
