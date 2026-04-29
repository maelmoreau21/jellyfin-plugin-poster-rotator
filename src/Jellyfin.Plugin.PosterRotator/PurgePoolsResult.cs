namespace Jellyfin.Plugin.PosterRotator;

public sealed class PurgePoolsResult
{
    public int DeletedCount { get; set; }

    public int SkippedCount { get; set; }

    public int FailedCount { get; set; }
}
