using MediaBrowser.Model.Plugins;
using System.Collections.Generic;

namespace Jellyfin.Plugin.PosterRotator;

public class LibraryRule
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public class Configuration : BasePluginConfiguration
{
    // Backwards-compatible simple list of library names (older versions)
    public List<string> Libraries { get; set; } = new();

    // Preferred shape for the UI: list of named rules with Enabled flag
    public List<LibraryRule> LibraryRules { get; set; } = new();

    public int PoolSize { get; set; } = 5;
    public bool SequentialRotation { get; set; } = false;
    public bool SaveNextToMedia { get; set; } = true;
    public bool LockImagesAfterFill { get; set; } = false;
    public bool DryRun { get; set; } = false;
    public List<string> ExtraPosterPatterns { get; set; } = new();
    public int MinHoursBetweenSwitches { get; set; } = 23;
    // If true, the plugin will attempt to trigger a library scan after rotating posters.
    // This is a heavy operation; default is true (will attempt to refresh libraries after rotation).
    public bool TriggerLibraryScanAfterRotation { get; set; } = true;
    // Manual list of library root paths to process. If non-empty, these paths will be used
    // to select which items the rotator will operate on. One path per entry.
    public List<string> ManualLibraryRoots { get; set; } = new();
}
