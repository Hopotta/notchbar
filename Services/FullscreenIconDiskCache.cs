using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NotchBar.Services;

/// <summary>
/// Small content-addressed PNG cache for application icons. Callers use stable executable
/// identity strings as keys; the executable metadata in that key makes updates miss old entries.
/// </summary>
internal sealed class FullscreenIconDiskCache
{
    internal const int DefaultMaximumEntries = 48;
    internal const int DefaultMaximumPngBytes = 128 * 1024;
    internal const long DefaultMaximumPngStorageBytes = 2 * 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private readonly object _sync = new();
    private readonly string _directory;
    private readonly int _maximumEntries;
    private readonly int _maximumPngBytes;
    private readonly long _maximumPngStorageBytes;
    private bool _cleanupIncomplete;

    internal FullscreenIconDiskCache(
        string directory,
        int maximumEntries = DefaultMaximumEntries,
        int maximumPngBytes = DefaultMaximumPngBytes,
        long maximumPngStorageBytes = DefaultMaximumPngStorageBytes)
    {
        _directory = directory;
        _maximumEntries = Math.Max(1, maximumEntries);
        _maximumPngBytes = Math.Max(33, maximumPngBytes);
        _maximumPngStorageBytes = Math.Max(0, maximumPngStorageBytes);
    }

    internal bool TryRead(string stableKey, out byte[] png)
    {
        png = [];
        if (string.IsNullOrWhiteSpace(stableKey))
        {
            return false;
        }

        lock (_sync)
        {
            var referencePath = GetReferencePath(stableKey);
            try
            {
                var referenceInfo = new FileInfo(referencePath);
                if (!referenceInfo.Exists || referenceInfo.Length != 64)
                {
                    TryDelete(referencePath);
                    return false;
                }

                var digest = File.ReadAllText(referencePath, Encoding.ASCII).Trim();
                if (!IsSha256Digest(digest))
                {
                    TryDelete(referencePath);
                    return false;
                }

                var contentPath = GetContentPath(digest);
                var contentInfo = new FileInfo(contentPath);
                if (!contentInfo.Exists || contentInfo.Length > _maximumPngBytes)
                {
                    TryDelete(referencePath);
                    return false;
                }

                var candidate = File.ReadAllBytes(contentPath);
                if (!IsValidPng(candidate) || !string.Equals(
                        Convert.ToHexString(SHA256.HashData(candidate)), digest, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(referencePath);
                    return false;
                }

                try
                {
                    File.SetLastWriteTimeUtc(referencePath, DateTime.UtcNow);
                }
                catch (IOException)
                {
                    // A cache hit remains usable when its access time cannot be refreshed.
                }
                catch (UnauthorizedAccessException)
                {
                    // A cache hit remains usable when its access time cannot be refreshed.
                }

                png = candidate;
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    internal void Remove(string stableKey)
    {
        if (string.IsNullOrWhiteSpace(stableKey))
        {
            return;
        }

        lock (_sync)
        {
            TryDelete(GetReferencePath(stableKey));
            try
            {
                if (Directory.Exists(_directory))
                {
                    Trim();
                }
            }
            catch (IOException)
            {
                // Invalid cache removal is best-effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Invalid cache removal is best-effort.
            }
        }
    }

    internal bool TryWrite(string stableKey, ReadOnlySpan<byte> png)
    {
        if (string.IsNullOrWhiteSpace(stableKey) || !IsValidPng(png))
        {
            return false;
        }

        lock (_sync)
        {
            var temporaryPaths = new List<string>(2);
            try
            {
                Directory.CreateDirectory(_directory);
                var keyDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableKey)));
                var contentDigest = Convert.ToHexString(SHA256.HashData(png));
                var contentPath = GetContentPath(contentDigest);
                var referencePath = Path.Combine(_directory, keyDigest + ".ref");

                if (!IsContentBlobValid(contentPath, contentDigest) && !TryWriteAtomically(contentPath, png, temporaryPaths))
                {
                    return false;
                }

                var referenceBytes = Encoding.ASCII.GetBytes(contentDigest);
                if (!TryWriteAtomically(referencePath, referenceBytes, temporaryPaths))
                {
                    return false;
                }

                Trim();
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (CryptographicException)
            {
                return false;
            }
            finally
            {
                foreach (var temporaryPath in temporaryPaths)
                {
                    TryDelete(temporaryPath);
                }
            }
        }
    }

    private bool IsValidPng(ReadOnlySpan<byte> png)
    {
        if (png.Length < 33 || png.Length > _maximumPngBytes || !png[..PngSignature.Length].SequenceEqual(PngSignature) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.Slice(8, 4)) != 13 ||
            !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        return width is > 0 and <= 256 && height is > 0 and <= 256;
    }

    private bool TryWriteAtomically(string destinationPath, ReadOnlySpan<byte> contents, ICollection<string> temporaryPaths)
    {
        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        temporaryPaths.Add(temporaryPath);
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            temporaryPaths.Remove(temporaryPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Trim()
    {
        _cleanupIncomplete = false;
        var references = GetReferences();
        while (references.Count > _maximumEntries)
        {
            TryDelete(references[0].Path);
            references.RemoveAt(0);
        }

        var referencedDigests = GetReferencedDigests(references);
        long pngBytes = referencedDigests.Sum(digest => GetFileLengthOrZero(GetContentPath(digest)));
        while (pngBytes > _maximumPngStorageBytes && references.Count > 0)
        {
            var oldest = references[0];
            TryDelete(oldest.Path);
            references.RemoveAt(0);
            referencedDigests = GetReferencedDigests(references);
            pngBytes = referencedDigests.Sum(digest => GetFileLengthOrZero(GetContentPath(digest)));
        }

        if (_cleanupIncomplete)
        {
            return;
        }

        foreach (var contentPath in Directory.EnumerateFiles(_directory, "*.png"))
        {
            var digest = Path.GetFileNameWithoutExtension(contentPath);
            if (!referencedDigests.Contains(digest))
            {
                TryDelete(contentPath);
            }
        }

        foreach (var temporaryPath in Directory.EnumerateFiles(_directory, "*.tmp"))
        {
            TryDelete(temporaryPath);
        }
    }

    private List<CacheReference> GetReferences()
    {
        var references = new List<CacheReference>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.ref"))
        {
            try
            {
                if (new FileInfo(path).Length != 64)
                {
                    TryDelete(path);
                    continue;
                }

                var digest = File.ReadAllText(path, Encoding.ASCII).Trim();
                if (!IsSha256Digest(digest) || !File.Exists(GetContentPath(digest)))
                {
                    TryDelete(path);
                    continue;
                }

                references.Add(new CacheReference(path, File.GetLastWriteTimeUtc(path), digest));
            }
            catch (IOException)
            {
                TryDelete(path);
            }
            catch (UnauthorizedAccessException)
            {
                // Leave an unreadable reference alone; cache writes still succeed or degrade safely.
                _cleanupIncomplete = true;
            }
        }

        references.Sort(static (left, right) => left.LastAccessUtc.CompareTo(right.LastAccessUtc));
        return references;
    }

    private static HashSet<string> GetReferencedDigests(IEnumerable<CacheReference> references) =>
        references.Select(reference => reference.Digest).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private string GetReferencePath(string stableKey)
    {
        var keyDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableKey)));
        return Path.Combine(_directory, keyDigest + ".ref");
    }

    private string GetContentPath(string digest) => Path.Combine(_directory, digest + ".png");

    private bool IsContentBlobValid(string path, string digest)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > _maximumPngBytes)
            {
                return false;
            }

            var contents = File.ReadAllBytes(path);
            return IsValidPng(contents) && string.Equals(
                Convert.ToHexString(SHA256.HashData(contents)), digest, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool IsSha256Digest(string digest) =>
        digest.Length == 64 && digest.All(Uri.IsHexDigit);

    private static long GetFileLengthOrZero(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cache cleanup is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Cache cleanup is best-effort.
        }
    }

    private sealed record CacheReference(string Path, DateTime LastAccessUtc, string Digest);
}
