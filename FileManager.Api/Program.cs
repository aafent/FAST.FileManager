using FAST.FileManager.Api;
using FAST.FileManager.Api.Endpoints;
using FAST.FileManager.Providers.Composite;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────

// Register the S3 file provider server-side.
builder.Services.AddAntiforgery();

builder.Services.AddCompositeFileProvider(providers =>
    ProviderConfigurationHelper.RegisterProviders(providers, builder.Configuration));


// Required for streaming downloads through the API.
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// ── Middleware ────────────────────────────────────────────────────────────────

// Serve the Blazor WASM client app.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseAntiforgery();

// ── API routes ────────────────────────────────────────────────────────────────
var api = app.MapGroup("/api/files");

api.MapGet   ("/volumes",           FileEndpoints.GetVolumes);
api.MapGet   ("/list",              FileEndpoints.List);
api.MapPost  ("/folder",            FileEndpoints.CreateFolder);
api.MapDelete("/item",              FileEndpoints.Delete);
api.MapPut   ("/rename",            FileEndpoints.Rename);
api.MapPut   ("/move",              FileEndpoints.Move);
api.MapPut   ("/copy",              FileEndpoints.Copy);
api.MapPut   ("/duplicate",         FileEndpoints.Duplicate);
api.MapPost  ("/upload",            FileEndpoints.Upload).DisableAntiforgery();
api.MapGet   ("/download",          FileEndpoints.Download);

// ── Fallback: serve the WASM app for all non-API routes ──────────────────────
app.MapFallbackToFile("index.html");

app.Run();
