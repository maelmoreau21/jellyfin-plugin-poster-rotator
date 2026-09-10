namespace Jellyfin.Plugin.PosterRotator.Helpers;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

/// <summary>
/// Lightweight image fingerprinting for duplicate detection.
/// </summary>
public static class ImageHash
{
    private const string HashFileName = "pool_hashes.json";
    public const int DefaultDuplicateThreshold = 10;
    public const int CurrentPosterMatchThreshold = 6;

    /// <summary>
    /// Compute a 64-bit content fingerprint for an image file.
    /// This is dependency-free and intentionally fast; for duplicate detection on
    /// downloaded images, prefer <see cref="ComputeNormalizedHashAsync"/>.
    /// </summary>
    public static ulong ComputeHash(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length == 0) return 0;

            using var fs = File.OpenRead(filePath);
            var fileLen = fs.Length;

            const int headerSkip = 64;
            var dataLen = fileLen - headerSkip;
            if (dataLen < 64)
            {
                var allBytes = File.ReadAllBytes(filePath);
                return ComputeFromBytes(allBytes);
            }

            var samples = new byte[64];
            var step = dataLen / 64;
            for (var i = 0; i < 64; i++)
            {
                fs.Seek(headerSkip + (i * step), SeekOrigin.Begin);
                var b = fs.ReadByte();
                samples[i] = b >= 0 ? (byte)b : (byte)0;
            }

            return ComputeFromBytes(samples);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Compute a perceptual hash (dHash / Difference Hash) after asking Jellyfin to
    /// normalize the image to a tiny 9×8 thumbnail. The dHash compares adjacent pixel
    /// luminance values to produce a 64-bit fingerprint that is robust against
    /// resolution, compression, and minor visual differences.
    /// </summary>
    public static async Task<ulong> ComputeNormalizedHashAsync(
        string filePath,
        IImageProcessor? imageProcessor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (imageProcessor == null)
            return ComputeHash(filePath);

        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length == 0)
                return 0;

            // Normalize to 9×8 — the minimum size for a dHash (9 wide × 8 tall = 8×8 = 64 bit comparisons)
            var processed = await imageProcessor.ProcessImage(new ImageProcessingOptions
            {
                Image = new ItemImageInfo
                {
                    Path = filePath,
                    Type = ImageType.Primary,
                    DateModified = info.LastWriteTimeUtc
                },
                MaxWidth = 9,
                MaxHeight = 8,
                Quality = 80,
                SupportedOutputFormats = imageProcessor.GetSupportedImageOutputFormats()
            }).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(processed.Path) && File.Exists(processed.Path))
                return ComputeDHash(processed.Path);
        }
        catch
        {
            // Fall back to the dependency-free fingerprint below.
        }

        return ComputeHash(filePath);
    }

    /// <summary>
    /// Compute a perceptual dHash (Difference Hash) from a small normalized image.
    /// Reads pixel data and compares adjacent pixel luminance to produce a 64-bit hash.
    /// For a 9×8 image: 8 rows × 8 horizontal comparisons = 64 bits.
    /// </summary>
    internal static ulong ComputeDHash(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return 0;

            var fileBytes = File.ReadAllBytes(filePath);
            if (fileBytes.Length < 72) // Need at least 9×8 = 72 sample points
                return ComputeFromBytes(fileBytes);

            // Sample 72 evenly-spaced bytes from the data portion (skip header)
            // and treat them as a 9-wide × 8-tall grid of luminance values.
            const int width = 9;
            const int height = 8;
            const int headerSkip = 32; // Skip file header metadata
            var dataLen = fileBytes.Length - headerSkip;
            if (dataLen < 72)
                return ComputeFromBytes(fileBytes);

            var luminance = new byte[width * height];
            var step = dataLen / (width * height);
            for (var i = 0; i < width * height; i++)
            {
                var pos = headerSkip + (i * step);
                luminance[i] = pos < fileBytes.Length ? fileBytes[pos] : (byte)0;
            }

            return ComputeDHashFromLuminance(luminance);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Compute a perceptual dHash (Difference Hash) from a 9×8 grayscale luminance array.
    /// For a 9×8 image: 8 rows × 8 horizontal comparisons = 64 bits.
    /// Each bit is 1 if pixel[col] > pixel[col+1], else 0.
    /// </summary>
    internal static ulong ComputeDHashFromLuminance(byte[] luminance)
    {
        if (luminance == null || luminance.Length < 72)
            return 0;

        const int width = 9;
        const int height = 8;
        ulong hash = 0;
        var bit = 0;

        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width - 1; col++) // 8 comparisons per row
            {
                if (luminance[(row * width) + col] > luminance[(row * width) + col + 1])
                    hash |= 1UL << bit;
                bit++;
            }
        }

        return hash;
    }

    private static ulong ComputeFromBytes(byte[] data)
    {
        if (data.Length == 0) return 0;

        var values = new byte[64];
        if (data.Length >= 64)
        {
            var step = data.Length / 64;
            for (var i = 0; i < 64; i++)
                values[i] = data[i * step];
        }
        else
        {
            Array.Copy(data, values, Math.Min(data.Length, 64));
        }

        long sum = 0;
        for (var i = 0; i < 64; i++) sum += values[i];
        var avg = (byte)(sum / 64);

        ulong hash = 0;
        for (var i = 0; i < 64; i++)
        {
            if (values[i] >= avg)
                hash |= 1UL << i;
        }

        return hash;
    }

    /// <summary>
    /// Compute Hamming distance between two hashes.
    /// </summary>
    public static int HammingDistance(ulong a, ulong b)
    {
        var diff = a ^ b;
        var count = 0;
        while (diff != 0)
        {
            count++;
            diff &= diff - 1;
        }

        return count;
    }

    /// <summary>
    /// Check if a hash is visually close to any existing hash.
    /// </summary>
    public static bool IsDuplicate(ulong hash, IEnumerable<ulong> existingHashes, int threshold = DefaultDuplicateThreshold)
    {
        if (hash == 0) return false;
        foreach (var existing in existingHashes)
        {
            if (existing == 0) continue;
            if (HammingDistance(hash, existing) <= threshold)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Load hashes from pool_hashes.json in the pool directory.
    /// </summary>
    public static Dictionary<string, ulong> LoadHashes(string poolDir)
    {
        var path = Path.Combine(poolDir, HashFileName);
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, ulong>>(json) ?? new();
            }
        }
        catch
        {
        }

        return new();
    }

    /// <summary>
    /// Save a hash entry to pool_hashes.json atomically.
    /// </summary>
    public static void SaveHash(string poolDir, string fileName, ulong hash)
    {
        var path = Path.Combine(poolDir, HashFileName);
        try
        {
            var map = LoadHashes(poolDir);
            map[fileName] = hash;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(map));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Remove a hash entry from pool_hashes.json.
    /// </summary>
    public static void RemoveHash(string poolDir, string fileName)
    {
        var path = Path.Combine(poolDir, HashFileName);
        try
        {
            var map = LoadHashes(poolDir);
            if (map.Remove(fileName))
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(map));
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch
        {
        }
    }
}
