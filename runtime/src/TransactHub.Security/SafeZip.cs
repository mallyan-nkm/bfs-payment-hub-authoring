using System.IO.Compression;

namespace TransactHub.Security;

/// <summary>
/// Reads a zip a caller sent (a package, a bundle, a backup) without trusting the sizes its headers claim (spec R-09-59). A
/// zip bomb, an archive of more entries than a package has, or one that expands past the limits is refused as
/// <see cref="InvalidDataException"/>, which every reader already turns into its own "not a zip" refusal. The limits are
/// well above the largest package the factory builds; a host may lower them, never lift them past what fits in memory.
/// </summary>
public static class SafeZip
{
    /// <summary>The most entries an archive may hold.</summary>
    public const int MaxEntries = 10_000;
    /// <summary>The most bytes one entry may expand to.</summary>
    public const long MaxEntryBytes = 64L * 1024 * 1024;
    /// <summary>The most bytes all entries together may expand to.</summary>
    public const long MaxTotalBytes = 256L * 1024 * 1024;

    /// <summary>Every file entry (directories skipped) that <paramref name="keep"/> takes, by its name, each read within the limits.</summary>
    public static Dictionary<string, byte[]> Read(byte[] zip, Func<string, bool>? keep = null, long maxEntryBytes = MaxEntryBytes, long maxTotalBytes = MaxTotalBytes, int maxEntries = MaxEntries)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var a = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        if (a.Entries.Count > maxEntries) throw new InvalidDataException($"The archive holds {a.Entries.Count} entries; at most {maxEntries} are read");
        long total = 0;
        foreach (var e in a.Entries)
        {
            if (e.FullName.EndsWith('/') || (keep is not null && !keep(e.FullName))) continue;
            using var s = e.Open();
            var bytes = ReadAtMost(s, Math.Min(maxEntryBytes, maxTotalBytes - total), e.FullName);
            total += bytes.Length;
            files[e.FullName] = bytes;
        }
        return files;
    }

    /// <summary>One stream, refused when it gives more than <paramref name="max"/> bytes, whatever its header said.</summary>
    public static byte[] ReadAtMost(Stream s, long max, string name)
    {
        using var m = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (m.Length + n > max) throw new InvalidDataException($"{name} expands past the size this host reads ({max} bytes)");
            m.Write(buf, 0, n);
        }
        return m.ToArray();
    }
}
