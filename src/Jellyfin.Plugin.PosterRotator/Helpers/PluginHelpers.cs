namespace Jellyfin.Plugin.PosterRotator.Helpers;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

/// <summary>
/// Shared utility methods and models used by both PosterRotatorService and PoolService.
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
}

/// <summary>
/// Shared rotation state model, used by both PosterRotatorService and PoolService.
/// Format: { "LastIndexByItem": { "guid": idx }, "LastRotatedUtcByItem": { "guid": epoch } }
/// </summary>
public sealed class RotationState
{
    public Dictionary<string, int> LastIndexByItem { get; set; } = new();
    public Dictionary<string, long> LastRotatedUtcByItem { get; set; } = new();
}
