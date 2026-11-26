using System.Text.RegularExpressions;

namespace VFlasher.Core;

/// <summary>
/// One image.conf file: plain KEY=value lines describing an OS image for a board.
/// Same format and validation rules as the Linux vflasher script.
/// </summary>
public sealed partial class ImageManifest
{
    static readonly string[] RequiredKeys =
        ["NAME", "IMAGE_URL", "IMAGE_FILE", "IMAGE_SHA256", "IMAGE_FORMAT", "FLASH_METHOD"];

    public static readonly string[] SupportedFormats = ["raw", "raw.xz", "raw.gz", "raw.zst"];

    readonly Dictionary<string, string> values;

    ImageManifest(string manufacturer, string board, string os, Dictionary<string, string> values)
    {
        Manufacturer = manufacturer;
        Board = board;
        Os = os;
        this.values = values;
    }

    public string Manufacturer { get; }
    public string Board { get; }
    public string Os { get; }

    public string Name => values["NAME"];
    public Uri ImageUrl => new(values["IMAGE_URL"]);
    public string ImageFile => values["IMAGE_FILE"];
    public string Sha256 => values["IMAGE_SHA256"];
    public string Format => values["IMAGE_FORMAT"];
    public string FlashMethod => values["FLASH_METHOD"];
    public long? ImageSize => long.TryParse(Get("IMAGE_SIZE"), out var n) ? n : null;
    public string? OsVersion => Get("OS_VERSION");
    public string? Dtb => Get("DTB");

    /// <summary>"qualcomm/iq9075-evk/fedora"</summary>
    public string Id => $"{Manufacturer}/{Board}/{Os}";

    public string? Get(string key) => values.TryGetValue(key, out var v) ? v : null;

    public IReadOnlyDictionary<string, string> Values => values;

    public static bool IsValidName(string name) => NameRegex().IsMatch(name);

    public static ImageManifest Parse(string manufacturer, string board, string os, string text)
    {
        foreach (var part in new[] { manufacturer, board, os })
            if (!IsValidName(part))
                throw new FormatException($"invalid name '{part}'");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineNo = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNo++;
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            var eq = line.IndexOf('=');
            if (eq < 0)
                throw new FormatException($"line {lineNo}: expected KEY=value");
            var key = line[..eq];
            if (!KeyRegex().IsMatch(key))
                throw new FormatException($"line {lineNo}: bad key '{key}'");
            values[key] = line[(eq + 1)..];
        }

        foreach (var key in RequiredKeys)
            if (string.IsNullOrEmpty(values.GetValueOrDefault(key)))
                throw new FormatException($"missing {key}");
        if (!FileNameRegex().IsMatch(values["IMAGE_FILE"]))
            throw new FormatException("unsafe IMAGE_FILE");
        if (!ShaRegex().IsMatch(values["IMAGE_SHA256"]))
            throw new FormatException("bad IMAGE_SHA256");
        if (!Uri.TryCreate(values["IMAGE_URL"], UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            throw new FormatException("IMAGE_URL must be an http(s) URL");

        return new ImageManifest(manufacturer, board, os, values);
    }

    public override string ToString() => Name;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")] private static partial Regex NameRegex();
    [GeneratedRegex("^[A-Z0-9_]+$")] private static partial Regex KeyRegex();
    [GeneratedRegex("^[A-Za-z0-9._+-]+$")] private static partial Regex FileNameRegex();
    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex ShaRegex();
}
