using FAST.FileManager.Abstractions;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FAST.FileManager.Providers.Api;

/// <summary>
/// Extension methods for registering the API-backed file provider with the
/// Blazor WASM dependency injection container.
/// </summary>
public static class ApiProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="ApiFileProvider"/> as the <see cref="IFileProvider"/>
    /// implementation. The HttpClient base address is set to the WASM app's
    /// own origin so relative API paths resolve correctly.
    /// </summary>
    public static IServiceCollection AddApiFileProvider(
        this IServiceCollection services)
    {
        services.AddHttpClient("FileManager.Api", (sp, client) =>
        {
            // Use the WASM host's base address (same origin as FileManager.Api server).
            var env = sp.GetRequiredService<IWebAssemblyHostEnvironment>();
            client.BaseAddress = new Uri(env.BaseAddress);
        });

        services.AddScoped<IFileProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient("FileManager.Api");
            return new ApiFileProvider(http);
        });

        return services;
    }
}
