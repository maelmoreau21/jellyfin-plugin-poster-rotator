using System.Linq;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterRotator.Api
{
    [ApiController]
    [Route("Plugins/PosterRotator/[controller]")]
    public class LibrariesController : ControllerBase
    {
        private readonly ILibraryManager _library;
        private readonly ILogger<LibrariesController> _log;

        public LibrariesController(ILibraryManager library, ILogger<LibrariesController> log)
        {
            _library = library;
            _log = log;
        }

        [HttpGet]
        public IActionResult Get()
        {
            try
            {
                // Reuse the same reflection-based logic as the service to return Name and Paths
                var map = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>(System.StringComparer.OrdinalIgnoreCase);

                var lmType = _library.GetType();
                var getVf = lmType.GetMethod("GetVirtualFolders");
                if (getVf != null)
                {
                    var vfResult = getVf.Invoke(_library, null) as System.Collections.IEnumerable;
                    if (vfResult != null)
                    {
                        foreach (var vf in vfResult)
                        {
                            try
                            {
                                var name = vf.GetType().GetProperty("Name")?.GetValue(vf) as string ?? "";
                                var locProp = vf.GetType().GetProperty("Locations") ?? vf.GetType().GetProperty("Paths");
                                var locVal = locProp?.GetValue(vf) as System.Collections.IEnumerable;
                                if (!map.ContainsKey(name)) map[name] = new System.Collections.Generic.List<string>();
                                if (locVal != null)
                                {
                                    foreach (var p in locVal)
                                    {
                                        if (p is string s && !string.IsNullOrWhiteSpace(s))
                                            map[name].Add(s);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }

                // dedupe
                foreach (var k in map.Keys.ToList())
                {
                    map[k] = map[k].Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
                }

                var result = map.Select(kv => new { Name = kv.Key, Paths = kv.Value }).ToList();
                return Ok(result);
            }
            catch (System.Exception ex)
            {
                _log.LogWarning(ex, "PosterRotator: LibrariesController failed to enumerate virtual folders");
                return StatusCode(500, "Failed to enumerate libraries");
            }
        }
    }
}
