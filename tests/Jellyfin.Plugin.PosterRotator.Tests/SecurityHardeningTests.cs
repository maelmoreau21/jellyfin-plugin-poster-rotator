using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PosterRotator.Api;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public sealed class SecurityHardeningTests
{
    [Fact]
    public async Task RemoteDownloads_DisableAutomaticRedirectsAndRevalidateRedirectLocations()
    {
        // Use public IP addresses so test does not rely on external DNS resolution
        var initialUrl = "https://93.184.216.34/poster.jpg";
        var forbiddenRedirectTarget = new Uri("http://192.168.1.100/secret.jpg");

        var handler = new TestRedirectHandler(request =>
        {
            if (request.RequestUri == new Uri(initialUrl))
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = forbiddenRedirectTarget;
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
        });

        using var client = new HttpClient(handler);
        var service = new PosterRotatorService(
            null!, null!, null!, null!, null!, null!,
            NullLogger<PosterRotatorService>.Instance);
        var cfg = new Configuration { BlockPrivateNetworkImageUrls = true };

        // 1. Verify that following a redirect to a forbidden private address is rejected
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendRemoteImageRequestAsync(client, initialUrl, cfg, CancellationToken.None));
        Assert.Equal("Remote image URL is not allowed.", exception.Message);

        // 2. Verify the HTTP client never sent a request to the forbidden redirect target
        Assert.Single(handler.RequestedUris);
        Assert.Equal(new Uri(initialUrl), handler.RequestedUris[0]);
        Assert.DoesNotContain(forbiddenRedirectTarget, handler.RequestedUris);

        // 3. Verify that a redirect to an authorized public address succeeds
        var allowedRedirectTarget = new Uri("https://93.184.216.35/actual_poster.jpg");
        var allowedHandler = new TestRedirectHandler(request =>
        {
            if (request.RequestUri == new Uri(initialUrl))
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = allowedRedirectTarget;
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
        });
        using var allowedClient = new HttpClient(allowedHandler);
        using var allowedResponse = await service.SendRemoteImageRequestAsync(allowedClient, initialUrl, cfg, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        Assert.Equal(2, allowedHandler.RequestedUris.Count);
        Assert.Equal(allowedRedirectTarget, allowedHandler.RequestedUris[1]);

        // 4. Verify ServiceRegistrator configures PosterRotator named client
        var services = new ServiceCollection();
        new ServiceRegistrator().RegisterServices(services, null!);
        using var serviceProvider = services.BuildServiceProvider();
        var clientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        using var registeredClient = clientFactory.CreateClient("PosterRotator");
        Assert.NotNull(registeredClient);
    }

    [Fact]
    public async Task UploadEndpoint_RejectsFilesAboveConfiguredLimitBeforeOpeningStream()
    {
        var localization = new PosterRotatorLocalization(() => "en");
        var controller = new PurgeController(null!, null!, localization);

        // Default MaxDownloadMegabytes is 25 MB.
        // A 26 MB file exceeds the configured limit.
        var largeFile = new MockUploadFile(length: 26L * 1024 * 1024);

        var result = await controller.UploadPoolImage(Guid.NewGuid(), largeFile, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Image is too large.", badRequest.Value);
        Assert.False(largeFile.StreamOpened, "File stream must not be opened when file exceeds size limit.");
    }

    [Fact]
    public void DownloadMissingPools_UsesDedicatedPluginDataPoolCreation()
    {
        var tempParent = Path.Combine(Path.GetTempPath(), "poster-rotator-sec-" + Guid.NewGuid().ToString("N"));
        var pluginData = Path.Combine(tempParent, "Jellyfin.Plugin.PosterRotator");
        Directory.CreateDirectory(pluginData);

        try
        {
            var store = new PoolStore(pluginData);
            var itemId = Guid.NewGuid();

            // 1. Verify TryCreatePoolDirectoryForWrite creates a directory on real disk
            var createdDir = store.TryCreatePoolDirectoryForWrite(itemId);

            Assert.NotNull(createdDir);
            Assert.True(Directory.Exists(createdDir), "Pool directory must exist on disk.");

            // 2. Verify the created path is strictly inside the dedicated plugin data pool store
            var expectedRoot = Path.Combine(tempParent, "Jellyfin.Plugin.PosterRotator.pools");
            var expectedDir = Path.Combine(expectedRoot, itemId.ToString("N"));
            Assert.Equal(expectedDir, createdDir);
            Assert.Equal(createdDir, store.TryGetPoolDirectory(itemId, create: false));

            // 3. Verify ResolvePoolDirectory with PluginData mode resolves to dedicated plugin data and ignores legacy folder
            var service = new PosterRotatorService(
                null!, null!, null!, store, null!, null!,
                NullLogger<PosterRotatorService>.Instance);
            var testItem = new Folder { Id = itemId };
            var legacyPoolDir = Path.Combine(tempParent, "legacy_media_folder", ".poster_pool");

            var resolvedDir = service.ResolvePoolDirectory(testItem, PoolStorageMode.PluginData, legacyPoolDir);

            Assert.Equal(createdDir, resolvedDir);
            Assert.NotEqual(legacyPoolDir, resolvedDir);
        }
        finally
        {
            if (Directory.Exists(tempParent))
            {
                try { Directory.Delete(tempParent, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void AdminApi_ExposesManualDownloadMissingPoolsAction()
    {
        var controllerType = typeof(PurgeController);

        // Verify controller attributes
        var apiControllerAttr = controllerType.GetCustomAttribute<ApiControllerAttribute>();
        Assert.NotNull(apiControllerAttr);

        var routeAttr = controllerType.GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(routeAttr);
        Assert.Equal("PosterRotator", routeAttr.Template);

        var authorizeAttr = controllerType.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorizeAttr);
        Assert.Equal(Policies.RequiresElevation, authorizeAttr.Policy);

        // Verify method DownloadMissingPools route and HTTP method
        var method = controllerType.GetMethod(nameof(PurgeController.DownloadMissingPools));
        Assert.NotNull(method);

        var httpPostAttr = method.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(httpPostAttr);
        Assert.Equal("Pools/DownloadMissing", httpPostAttr.Template);

        Assert.Equal(typeof(Task<ActionResult<PoolDownloadResult>>), method.ReturnType);
    }

    [Fact]
    public void RuntimeIgnoresLegacyManualLibraryRoots()
    {
        var cfg = new Configuration
        {
            ManualLibraryRoots = new List<string> { @"C:\Legacy\Movies", @"/opt/legacy/tv" }
        };

        var libraryManager = DispatchProxy.Create<ILibraryManager, DynamicLibraryManagerProxy>();
        ((DynamicLibraryManagerProxy)(object)libraryManager).Handler = (method, _) =>
        {
            if (method.Name == nameof(ILibraryManager.GetVirtualFolders))
            {
                var folderType = method.ReturnType.GetGenericArguments()[0];
                var folder = Activator.CreateInstance(folderType);
                folderType.GetProperty("Name")?.SetValue(folder, "Movies");
                folderType.GetProperty("Locations")?.SetValue(folder, new[] { @"C:\Media\Movies" });

                var listType = typeof(List<>).MakeGenericType(folderType);
                var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
                list.Add(folder);
                return list;
            }
            return null;
        };

        var service = new PosterRotatorService(
            libraryManager, null!, null!, null!, null!, null!,
            NullLogger<PosterRotatorService>.Instance);

        var scope = service.ResolveRunScope(cfg);

        // 1. Verify cfg.ManualLibraryRoots is cleared at runtime
        Assert.Empty(cfg.ManualLibraryRoots);

        // 2. Verify runtime selection ignores legacy manual paths and uses real Jellyfin library roots
        Assert.DoesNotContain(@"C:\Legacy\Movies", scope.Selection.Paths);
        Assert.DoesNotContain(@"/opt/legacy/tv", scope.Selection.Paths);
        Assert.Contains(@"C:\Media\Movies", scope.Selection.Paths);
    }

    private sealed class TestRedirectHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;
        public List<Uri> RequestedUris { get; } = new();

        public TestRedirectHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed class MockUploadFile : IFormFile
    {
        public bool StreamOpened { get; private set; }
        public long Length { get; }
        public string ContentType => "image/jpeg";
        public string ContentDisposition => "form-data; name=\"file\"; filename=\"large.jpg\"";
        public IHeaderDictionary Headers => new HeaderDictionary();
        public string Name => "file";
        public string FileName => "large.jpg";

        public MockUploadFile(long length)
        {
            Length = length;
        }

        public void CopyTo(Stream target) => throw new NotSupportedException();
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Stream OpenReadStream()
        {
            StreamOpened = true;
            throw new InvalidOperationException("Stream was opened unexpectedly when file size exceeds limit.");
        }
    }

    public class DynamicLibraryManagerProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return Handler?.Invoke(targetMethod!, args);
        }
    }
}
