using Microsoft.Extensions.DependencyInjection;
using MediaBrowser.Controller;                 // IServerApplicationHost
using MediaBrowser.Controller.Plugins;         // IPluginServiceRegistrator
using Jellyfin.Plugin.PosterRotator;

namespace Jellyfin.Plugin.PosterRotator
{
    // Must have a parameterless constructor
    public sealed class ServiceRegistrator : IPluginServiceRegistrator
    {
        // Jellyfin will call this at startup; register your services here
        public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
        {
            services.AddSingleton<PosterRotatorService>();
        }
    }
}
