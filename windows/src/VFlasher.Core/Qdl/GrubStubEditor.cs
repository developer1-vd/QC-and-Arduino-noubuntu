using System.Text;
using DiscUtils.Fat;

namespace VFlasher.Core.Qdl;

/// <summary>
/// Makes GRUB load a given device tree, by adding
/// <c>devicetree ($root)/dtb/&lt;dtb&gt;</c> to the small GRUB configs on the EFI
/// partition, right after they locate /boot. /boot/dtb always points at the
/// newest kernel's device trees, so this keeps working after kernel updates.
/// Same change as the Linux vflasher script makes.
/// </summary>
public static class GrubStubEditor
{
    static readonly string[] Stubs = [@"\EFI\fedora\grub.cfg", @"\EFI\BOOT\grub.cfg"];

    /// <returns>The stubs that were changed.</returns>
    public static List<string> AddDevicetree(string espImagePath, string dtb)
    {
        var line = $"devicetree ($root)/dtb/{dtb}";
        var changed = new List<string>();
        var found = false;
        using var stream = new FileStream(espImagePath, FileMode.Open, FileAccess.ReadWrite);
        using (var fat = new FatFileSystem(stream))
        {
            foreach (var path in Stubs)
            {
                if (!fat.FileExists(path))
                    continue;
                found = true;
                string text;
                using (var f = fat.OpenFile(path, FileMode.Open, FileAccess.Read))
                using (var r = new StreamReader(f, Encoding.UTF8))
                    text = r.ReadToEnd();
                if (text.Split('\n').Any(l => l.StartsWith("devicetree ", StringComparison.Ordinal)))
                    continue;

                var lines = text.Split('\n').ToList();
                if (lines.Count > 0 && lines[^1].Length == 0)
                    lines.RemoveAt(lines.Count - 1);
                var output = new StringBuilder();
                var inserted = false;
                foreach (var l in lines)
                {
                    output.Append(l).Append('\n');
                    if (l.StartsWith("set prefix=", StringComparison.Ordinal))
                    {
                        output.Append(line).Append('\n');
                        inserted = true;
                    }
                }
                if (!inserted)
                    throw new InvalidDataException($"unexpected GRUB stub in {path}; not changing it");

                using (var f = fat.OpenFile(path, FileMode.Create, FileAccess.Write))
                {
                    var bytes = Encoding.UTF8.GetBytes(output.ToString());
                    f.Write(bytes);
                }
                changed.Add(path);
            }
        }
        if (!found)
            throw new InvalidDataException("no GRUB stub found on the image's EFI partition");
        return changed;
    }
}
