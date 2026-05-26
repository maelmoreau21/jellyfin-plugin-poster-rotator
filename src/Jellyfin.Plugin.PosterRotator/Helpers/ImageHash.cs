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
    /// Compute a hash after asking Jellyfin to normalize the image to a small poster
    /// preview. This makes duplicate detection less sensitive to provider metadata,
    /// source dimensions, and encoding differences.
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

            var processed = await imageProcessor.ProcessImage(new ImageProcessingOptions
            {
                Image = new ItemImageInfo
                {
                    Path = filePath,
                    Type = ImageType.Primary,
                    DateModified = info.LastWriteTimeUtc
                },
                MaxWidth = 64,
                MaxHeight = 96,
                Quality = 80,
                SupportedOutputFormats = imageProcessor.GetSupportedImageOutputFormats()
            }).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(processed.Path) && File.Exists(processed.Path))
                return ComputeHash(processed.Path);
        }
        catch
        {
            // Fall back to the dependency-free fingerprint below.
        }

        return ComputeHash(filePath);
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
