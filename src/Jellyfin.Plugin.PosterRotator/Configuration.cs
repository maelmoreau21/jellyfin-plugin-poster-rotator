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
}
