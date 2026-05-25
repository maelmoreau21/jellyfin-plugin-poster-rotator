using Microsoft.Extensions.DependencyInjection;
using MediaBrowser.Controller;                 // IServerApplicationHost
using MediaBrowser.Controller.Plugins;         // IPluginServiceRegistrator
using MediaBrowser.Model.Tasks;
using Jellyfin.Plugin.PosterRotator;
using System.Net.Http;

namespace Jellyfin.Plugin.PosterRotator
{
    // Must have a parameterless constructor
    public sealed class ServiceRegistrator : IPluginServiceRegistrator
    {
        // Jellyfin will call this at startup; register your services here
        public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
        {
            services.AddHttpClient("PosterRotator")
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AllowAutoRedirect = false
                });
            services.AddSingleton<PoolStore>();
            services.AddSingleton<PosterRotatorService>();
            services.AddSingleton<IPosterRotatorService>(sp => sp.GetRequiredService<PosterRotatorService>());
            services.AddSingleton<IScheduledTask, PosterRotationTask>();
            services.AddSingleton<IScheduledTask, DownloadMissingPoolsTask>();
            services.AddSingleton<IScheduledTask, OrphanPoolCleanupTask>();
        }
    }
}
