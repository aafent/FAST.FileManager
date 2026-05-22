using FAST.FileManager.Providers.Api;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<FAST.FileManager.App.App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Register the API-backed file provider.
// All S3 credentials stay on the server (FileManager.Api).
builder.Services.AddApiFileProvider();

await builder.Build().RunAsync();
