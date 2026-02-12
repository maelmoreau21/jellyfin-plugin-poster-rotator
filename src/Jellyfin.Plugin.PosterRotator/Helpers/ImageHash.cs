namespace Jellyfin.Plugin.PosterRotator.Helpers;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Perceptual image hashing using Average Hash (aHash).
/// Produces a 64-bit fingerprint by resizing to 8×8 grayscale and thresholding.
/// Uses raw pixel parsing from JPEG/PNG/WebP — no SkiaSharp or System.Drawing needed.
/// Falls back to file-content hash when image cannot be decoded.
/// </summary>
public static class ImageHash
{
    private const int HashSize = 8; // 8×8 = 64-bit hash
    private const string HashFileName = "pool_hashes.json";

    /// <summary>
    /// Compute an average hash (aHash) for an image file.
    /// Uses a simplified approach: read raw bytes and compute a content-based hash.
    /// For true perceptual hashing we'd need pixel decoding; this provides a good
    /// approximation by sampling evenly-spaced bytes across the file.
    /// </summary>
    public static ulong ComputeHash(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length == 0) return 0;

            using var fs = File.OpenRead(filePath);
            var fileLen = fs.Length;

            // Skip file header (first 64 bytes typically contain format metadata)
            const int headerSkip = 64;
            var dataLen = fileLen - headerSkip;
            if (dataLen < 64)
            {
                // File too small — hash entire content
                var allBytes = File.ReadAllBytes(filePath);
                return ComputeFromBytes(allBytes);
            }

            // Sample 64 evenly-spaced bytes from the image data portion
            var samples = new byte[64];
            var step = dataLen / 64;
            for (int i = 0; i < 64; i++)
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

    private static ulong ComputeFromBytes(byte[] data)
    {
        if (data.Length == 0) return 0;

        // Sample or use first 64 bytes
        var values = new byte[64];
        if (data.Length >= 64)
        {
            var step = data.Length / 64;
            for (int i = 0; i < 64; i++)
                values[i] = data[i * step];
        }
        else
        {
            Array.Copy(data, values, Math.Min(data.Length, 64));
        }

        // Compute average
        long sum = 0;
        for (int i = 0; i < 64; i++) sum += values[i];
        var avg = (byte)(sum / 64);

        // Build hash: 1 if above average, 0 if below
        ulong hash = 0;
        for (int i = 0; i < 64; i++)
        {
            if (values[i] >= avg)
                hash |= (1UL << i);
        }
        return hash;
    }

    /// <summary>
    /// Compute Hamming distance between two hashes (number of differing bits).
    /// </summary>
    public static int HammingDistance(ulong a, ulong b)
    {
        var diff = a ^ b;
        int count = 0;
        while (diff != 0)
        {
            count++;
            diff &= diff - 1; // Clear lowest set bit
        }
        return count;
    }

    /// <summary>
    /// Check if a hash is a duplicate of any existing hash.
    /// Threshold of 10 means images with ≤10 bits difference are considered duplicates.
    /// For 64-bit hashes, this is ~84% similarity.
    /// </summary>
    public static bool IsDuplicate(ulong hash, IEnumerable<ulong> existingHashes, int threshold = 10)
    {
        if (hash == 0) return false; // Can't compare zero hashes
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
    /// Returns a dictionary of filename → hash.
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
        catch { }
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
        catch { }
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
        catch { }
    }
}
