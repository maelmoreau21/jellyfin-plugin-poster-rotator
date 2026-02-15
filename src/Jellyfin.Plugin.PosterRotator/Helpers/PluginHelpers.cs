namespace Jellyfin.Plugin.PosterRotator.Helpers;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

/// <summary>
/// Shared utility methods and models used by PosterRotatorService.
/// </summary>
public static class PluginHelpers
{
    /// <summary>
    /// Supported image file extensions.
    /// </summary>
    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    /// <summary>
    /// Guess file extension from a URL. Returns null if unknown.
    /// </summary>
    public static string? GuessExtFromUrl(string url)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(ext)) return null;
            if (ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return ".jpg";
            return ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>
    /// Guess file extension from a MIME type. Returns null if unknown.
    /// </summary>
    public static string? GuessExtFromMime(string? mime) =>
        mime switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            _ => null
        };

    /// <summary>
    /// Format a byte count into a human-readable string (e.g. "1.5 MB").
    /// </summary>
    public static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Get the content type for an image file name.
    /// </summary>
    public static string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// Resolve the directory containing a media item's files.
    /// </summary>
    public static string? GetItemDirectory(string? itemPath)
    {
        if (string.IsNullOrEmpty(itemPath)) return null;
        if (File.Exists(itemPath)) return Path.GetDirectoryName(itemPath);
        if (Directory.Exists(itemPath)) return itemPath;
        return Path.GetDirectoryName(itemPath);
    }

    /// <summary>
    /// Load rotation state from a JSON file. Returns empty state if file is missing or corrupt.
    /// </summary>
    public static RotationState LoadRotationState(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<RotationState>(json) ?? new RotationState();
            }
        }
        catch { /* ignore corrupt state */ }

        return new RotationState();
    }

    /// <summary>
    /// Save rotation state to a JSON file atomically (write to .tmp then rename).
    /// </summary>
    public static void SaveRotationState(string path, RotationState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Atomically update a key-value JSON file (read-modify-write with .tmp rename).
    /// Used for pool_languages.json and similar metadata files.
    /// </summary>
    public static void UpdateJsonMapFile(string filePath, string key, string value)
    {
        try
        {
            Dictionary<string, string> map;
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                map = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
            else
            {
                map = new Dictionary<string, string>();
            }

            map[key] = value;

            var tmp = filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(map));
            File.Move(tmp, filePath, overwrite: true);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Load a key-value JSON map file. Returns empty dictionary if missing.
    /// </summary>
    public static Dictionary<string, string> LoadJsonMap(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
        }
        catch { }
        return new Dictionary<string, string>();
    }

    /// <summary>
    /// Read a key-value JSON map file and count entries matching a predicate.
    /// Returns 0 if the file is missing or corrupt.
    /// </summary>
    public static int CountInJsonMap(string filePath, Func<KeyValuePair<string, string>, bool> predicate)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                return map?.Count(predicate) ?? 0;
            }
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// Get image dimensions by reading file headers only (no full decode).
    /// Supports JPEG (SOF markers), PNG (IHDR), WebP (VP8/VP8L), GIF (header).
    /// Returns (0,0) if format is unrecognized or file is corrupt.
    /// </summary>
    public static (int Width, int Height) GetImageDimensions(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            var header = new byte[30];
            if (fs.Read(header, 0, header.Length) < 8) return (0, 0);

            // PNG: 89 50 4E 47 ... IHDR at offset 16 (width BE), 20 (height BE)
            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            {
                if (fs.Length < 24) return (0, 0);
                int w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                int h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                return (w, h);
            }

            // GIF: GIF87a/GIF89a — width LE at 6, height LE at 8
            if (header[0] == 'G' && header[1] == 'I' && header[2] == 'F')
            {
                int w = header[6] | (header[7] << 8);
                int h = header[8] | (header[9] << 8);
                return (w, h);
            }

            // WebP: RIFF....WEBP
            if (header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
                && header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P')
            {
                // VP8 lossy: at offset 26, width LE 16-bit & height LE 16-bit
                if (header[12] == 'V' && header[13] == 'P' && header[14] == '8' && header[15] == ' ')
                {
                    var buf = new byte[10];
                    fs.Seek(23, SeekOrigin.Begin);
                    if (fs.Read(buf, 0, 10) >= 10)
                    {
                        // Skip frame tag (3 bytes) + start code (3 bytes) then dims at +6
                        int w = (buf[6] | (buf[7] << 8)) & 0x3FFF;
                        int h = (buf[8] | (buf[9] << 8)) & 0x3FFF;
                        return (w, h);
                    }
                }
                // VP8L lossless: at offset 21, 14-bit width + 14-bit height packed
                if (header[12] == 'V' && header[13] == 'P' && header[14] == '8' && header[15] == 'L')
                {
                    var buf = new byte[5];
                    fs.Seek(21, SeekOrigin.Begin);
                    if (fs.Read(buf, 0, 5) >= 5 && buf[0] == 0x2F)
                    {
                        int bits = buf[1] | (buf[2] << 8) | (buf[3] << 16) | (buf[4] << 24);
                        int w = (bits & 0x3FFF) + 1;
                        int h = ((bits >> 14) & 0x3FFF) + 1;
                        return (w, h);
                    }
                }
                return (0, 0);
            }

            // JPEG: scan for SOF markers (0xFF 0xC0..0xCF except 0xC4, 0xC8, 0xCC)
            if (header[0] == 0xFF && header[1] == 0xD8)
            {
                fs.Seek(2, SeekOrigin.Begin);
                var buf = new byte[9];
                while (fs.Position < fs.Length - 9)
                {
                    // Find marker
                    int b = fs.ReadByte();
                    if (b != 0xFF) continue;
                    while (b == 0xFF && fs.Position < fs.Length) b = fs.ReadByte();
                    if (b < 0) break;

                    // SOF marker? (C0-CF except C4, C8, CC)
                    if (b >= 0xC0 && b <= 0xCF && b != 0xC4 && b != 0xC8 && b != 0xCC)
                    {
                        if (fs.Read(buf, 0, 7) >= 7)
                        {
                            int h = (buf[3] << 8) | buf[4];
                            int w = (buf[5] << 8) | buf[6];
                            return (w, h);
                        }
                        break;
                    }
                    // Skip segment
                    if (fs.Read(buf, 0, 2) < 2) break;
                    int segLen = (buf[0] << 8) | buf[1];
                    if (segLen < 2) break;
                    fs.Seek(segLen - 2, SeekOrigin.Current);
                }
            }
        }
        catch { }
        return (0, 0);
    }

    /// <summary>
    /// Retry an async operation with exponential backoff (1s, 2s, 4s).
    /// Retries on HttpRequestException and transient HTTP status codes (429, 500-599).
    /// </summary>
    public static async Task<T> RetryAsync<T>(
        Func<Task<T>> action, int maxRetries, ILogger? log, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries && !ct.IsCancellationRequested)
            {
                attempt++;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)); // 1s, 2s, 4s
                log?.LogDebug("PosterRotator: retry {Attempt}/{Max} after {Delay}s — {Error}",
                    attempt, maxRetries, delay.TotalSeconds, ex.Message);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
        }
    }

    /// <summary>Overload for void-returning async tasks.</summary>
    public static async Task RetryAsync(
        Func<Task> action, int maxRetries, ILogger? log, CancellationToken ct)
    {
        await RetryAsync(async () => { await action().ConfigureAwait(false); return 0; },
            maxRetries, log, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Shared rotation state model, used by PosterRotatorService.
/// Format: { "LastIndexByItem": { "guid": idx }, "LastRotatedUtcByItem": { "guid": epoch } }
/// </summary>
public sealed class RotationState
{
    public Dictionary<string, int> LastIndexByItem { get; set; } = new();
    public Dictionary<string, long> LastRotatedUtcByItem { get; set; } = new();
}
