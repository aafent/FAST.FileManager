# FileManager

A dependency-free Blazor WASM file manager component and a transport-agnostic
file provider abstraction. Provider implementations (e.g. S3) ship as separate
packages.

# FAST FileManager — Solution Wiki

## Overview

FAST FileManager is a Blazor WebAssembly file manager component system designed for NuGet distribution. It follows a clean, layered architecture built around a single transport-agnostic interface (`IFileProvider`). The component never depends on a specific backend — it only knows about that interface. Concrete providers (S3, local filesystem, HTTP proxy) implement it independently.

The solution consists of six .NET projects. Three are published as NuGet packages; three are infrastructure (host, demo, tester) that are never packaged.

All namespaces follow the `FAST.FileManager.*` convention.

---

## Projects

### `FileManager` → NuGet: `FAST.FileManager`

**Role:** Core package. Everything else depends on this.

**Targets:** `net8.0`, `net9.0`

**Contains three layers:**

#### Abstractions (`FAST.FileManager.Abstractions`)

The contract every provider must implement. No concrete dependencies — pure interfaces and value types.

| Type | Purpose |
|---|---|
| `IFileProvider` | The central interface. Every provider implements this. |
| `Capabilities` | Declares which operations a provider supports (create folder, delete, rename, move, copy, upload, download). The component reads this at startup and hides or disables unsupported actions accordingly. Includes `Capabilities.Full` and `Capabilities.ReadOnly` convenience instances. |
| `FileOperationResult` / `FileOperationResult<T>` | Discriminated result type. Providers never throw for expected failures — they return a result with an error code instead. |
| `FileOperationError` | Enum of well-known error codes (`NotFound`, `Conflict`, `PermissionDenied`, `NotSupported`, `Unknown`). |
| `StorageItem` | Represents a file or folder returned by `ListAsync`. Contains key, name, kind, size, last modified. |
| `StorageItemKind` | Enum: `File` or `Folder`. |
| `Volume` | A top-level bucket or root that appears in the component's left panel. Has a key and display name. |
| `StructuredKey` | An address composed of volume + path + name. Used to navigate the hierarchy without string parsing. |

**`IFileProvider` operations:**

| Method | Description |
|---|---|
| `GetCapabilities()` | Returns what this provider can do. |
| `GetVolumesAsync()` | Lists top-level volumes (buckets, roots). |
| `ListAsync(folder)` | Lists the immediate contents of a folder. |
| `CreateFolderAsync(parent, name)` | Creates a new folder. |
| `DeleteAsync(item)` | Deletes a file or folder (recursive for folders). |
| `RenameAsync(item, newName)` | Renames in place. |
| `MoveAsync(item, targetFolder)` | Moves to a different folder. |
| `CopyAsync(item, targetFolder)` | Copies to a different folder. |
| `DuplicateAsync(item, newName)` | Copies within the same folder under a new name. |
| `UploadAsync(targetFolder, name, stream, contentType)` | Uploads a file from a stream. |
| `DownloadAsync(item)` | Returns the file content as a readable stream. |

#### SDK (`FAST.FileManager.SDK`)

A high-level developer API for use in application code (not the component itself). Similar in spirit to .NET's `File` and `Directory` classes — provider-agnostic, works over any `IFileProvider`.

| Type | Purpose |
|---|---|
| `FileManagerClient` | Fluent client for file and folder operations. Addresses items by volume + path + name. |
| `StorageCatalog` | Scoped to a specific volume and path. Used for listing and existence checks. |
| `FileReference` | A resolved reference to a specific file. Passed to `FileManagerClient` operations. |
| `FolderReference` | A resolved reference to a specific folder. |

#### Component (`FAST.FileManager.Components`)

The Blazor UI component itself.

| File | Purpose |
|---|---|
| `FileManagerComponent.razor` | The root component. Accepts an `IFileProvider` parameter. Renders the full file manager UI. |
| `FmBreadcrumb.razor` | Navigation breadcrumb bar. |
| `FmToolbar.razor` | Action toolbar (New Folder, Upload, Download, Rename, Cut, Copy, Paste, Delete). |
| `FmBusy.razor` | Loading overlay shown during async operations. |
| `FmModal.razor` | Generic modal dialog for rename and other prompts. |

**Static assets** (served via `_content/FAST.FileManager/` in any consuming app):

- `FileManager.css` — component styles
- `FileManager.js` — JS interop module (lazy-loaded via `import()`)

**Basic usage:**

```razor
@using FAST.FileManager.Components

<FileManagerComponent Provider="@_provider" />
```

---

### `FileManager.Providers` → NuGet: `FAST.FileManager.Providers`

**Role:** Server-side provider implementations. Install this in ASP.NET Core host projects. Never install in a Blazor WASM client.

**Targets:** `net8.0`, `net9.0`

**Depends on:** `FAST.FileManager`

#### S3 Provider (`FAST.FileManager.Providers.S3`)

An S3-compatible file provider with a hand-rolled SigV4 signing client — no AWS SDK dependency.

Validated against Cloudflare R2, MinIO, and AWS S3.

| Type | Purpose |
|---|---|
| `S3FileProvider` | Implements `IFileProvider` over any S3-compatible endpoint. |
| `S3Client` | Low-level HTTP client for S3 operations. Handles signing, path-style addressing, and error translation. Supports `ILogger` for HTTP diagnostics. |
| `SigV4Signer` | Hand-rolled AWS Signature Version 4 implementation. No external dependencies. |
| `S3ProviderOptions` | Configuration model. Bind from `appsettings.json`. |
| `MimeType` | Internal MIME type lookup by file extension. |
| `S3ProviderServiceCollectionExtensions` | `AddS3Provider()` DI extension. |

**`S3ProviderOptions` fields:**

| Field | Description |
|---|---|
| `Endpoint` | Base URL of the S3-compatible endpoint (no trailing slash). |
| `Region` | AWS region string. Use `us-east-1` for MinIO unless configured otherwise. |
| `AccessKey` | Access key ID. Optional when `UseBearerAuth` is true. |
| `SecretKey` | Secret access key, or Bearer token when `UseBearerAuth` is true. |
| `Buckets` | Explicit list of bucket names to expose as volumes. Recommended for Cloudflare R2. When empty, falls back to `ListBuckets`. |
| `VirtualHostedStyle` | When true, uses virtual-hosted-style URLs instead of path-style. |
| `UseHttp` | When true, uses HTTP instead of HTTPS. |
| `UseBearerAuth` | When true, authenticates with `Authorization: Bearer {SecretKey}` instead of SigV4. For use with FAST.FileRepository and other token-auth backends. |

**Known behaviour notes:**

- Folder semantics are emulated using key prefixes (S3 has no native folders).
- The canonical URI builder calls `Uri.UnescapeDataString` before `UriEncode` to avoid double-encoding of parentheses and spaces in object keys.
- `Debug` log level logs method + URL + status. `Trace` log level adds all headers plus a live `.http` snippet valid within the SigV4 15-minute window.

#### Local File System Provider (`FAST.FileManager.Providers.LocalFileSystem`)

Exposes a local disk path as a file provider volume. Useful for development, on-premise deployments, or hybrid setups where some volumes are local and others are S3.

| Type | Purpose |
|---|---|
| `LocalFileSystemProvider` | Implements `IFileProvider` over a local directory. |
| `LocalFileSystemOptions` | Configuration model. Bind from `appsettings.json`. |
| `LocalFileSystemServiceCollectionExtensions` | `AddLocalFileSystemProvider()` DI extension. |

#### Composite Provider (`FAST.FileManager.Providers.Composite`)

Aggregates multiple providers behind a single `IFileProvider`. Each registered provider exposes its own volumes; the composite routes operations to the correct provider based on the volume in the `StructuredKey`.

This is the normal production setup: one `CompositeFileProvider` backed by whatever combination of S3 and local providers is configured.

| Type | Purpose |
|---|---|
| `CompositeFileProvider` | Implements `IFileProvider` by delegating to registered sub-providers. |
| `CompositeProviderBuilder` | Builder used during DI registration to wire up sub-providers. This is the authoritative wiring point for provider construction — not the individual `Add*Provider()` extension methods. |
| `CompositeFileProviderExtensions` | `AddCompositeFileProvider()` DI extension. |
| `ProviderRegistration` | Internal record linking an alias to a provider instance. |

---

### `FileManager.Providers.Api` → NuGet: `FAST.FileManager.Providers.Api`

**Role:** WASM-side HTTP provider. Install this in Blazor WASM client projects that talk to a `FileManager.Api` backend. Never install server-side.

**Targets:** `net8.0`, `net9.0`

**Depends on:** `FAST.FileManager`

| Type | Purpose |
|---|---|
| `ApiFileProvider` | Implements `IFileProvider` by forwarding every call to the `FileManager.Api` REST endpoints over HTTP. Handles serialization, error translation, and upload/download streaming. |
| `ApiDtos` | Internal DTO types matching the `FileManager.Api` JSON contract. |
| `ApiProviderServiceCollectionExtensions` | `AddApiFileProvider()` DI extension. Registers an `HttpClient` named `FileManager.Api` with the WASM host's base address as the base URL, then registers `ApiFileProvider` as the scoped `IFileProvider`. |

**Registration (in `FileManager.App/Program.cs`):**

```csharp
builder.Services.AddApiFileProvider();
```

No configuration needed — the provider automatically uses the WASM app's own origin as the API base address via `IWebAssemblyHostEnvironment`.

---

### `FileManager.Api` — Not packaged

**Role:** ASP.NET Core host. Serves the Blazor WASM app and exposes the file provider REST API. Acts as a backend proxy so WASM clients never need to hold S3 credentials or deal with CORS.

**Targets:** `net9.0` only

**Key types:**

| Type | Purpose |
|---|---|
| `FileEndpoints` | Minimal API endpoint handlers for all file operations. |
| `ProviderConfigurationHelper` | Auto-detects single vs. multi-instance provider config from `appsettings.json` by inspecting whether a section has `Endpoint`/`RootPath` directly (single) or named subsections (multi). Eliminates manual `AddS3Provider()` calls in `Program.cs`. |
| `DtoMapper` | Maps between internal `StorageItem`/`Volume` types and the API JSON DTOs. |

**`appsettings.json` — single S3 instance:**

```json
"S3": {
  "Endpoint": "https://your-account.r2.cloudflarestorage.com",
  "Region": "auto",
  "AccessKey": "...",
  "SecretKey": "...",
  "Buckets": ["my-bucket"]
}
```

**`appsettings.json` — multiple S3 instances:**

```json
"S3": {
  "Primary":   { "Endpoint": "https://account1.r2...", "Buckets": ["bucket-a"] },
  "Secondary": { "Endpoint": "https://account2.r2...", "Buckets": ["bucket-b"] }
}
```

Each subsection key (lowercased) becomes the provider alias.

**REST endpoints exposed:**

| Method | Path | Operation |
|---|---|---|
| GET | `/api/files/volumes` | List volumes |
| GET | `/api/files/list` | List folder contents |
| POST | `/api/files/folder` | Create folder |
| DELETE | `/api/files/item` | Delete file or folder |
| PUT | `/api/files/rename` | Rename |
| PUT | `/api/files/move` | Move |
| PUT | `/api/files/copy` | Copy |
| PUT | `/api/files/duplicate` | Duplicate |
| POST | `/api/files/upload` | Upload file |
| GET | `/api/files/download` | Download file |

**Important:** `FileManager.Api.csproj` must have a **direct** `ProjectReference` to `FileManager.csproj`. Static web assets from a Razor Class Library are not propagated transitively — the host project must reference the RCL directly for `_content/FAST.FileManager/` to be served.

---

### `FileManager.App` — Not packaged

**Role:** Blazor WASM demo and development harness. References `FAST.FileManager.Providers.Api` and registers `ApiFileProvider`. Hosted inside `FileManager.Api`.

**Targets:** `net9.0` only

In development, `FileManager.Api` serves this app and the file manager component is exercised end-to-end through the API proxy. This project is not intended for production use as-is — it is the reference integration showing how to wire the component in a WASM app.

---

### `FileManager.Tester` — Not packaged

**Role:** Console integration tester. Exercises the S3 provider directly (no HTTP, no Blazor) against a real backend. Used to validate SigV4 signing, bucket operations, upload/download, and edge cases.

**Targets:** `net9.0` only

Validated against Cloudflare R2. Useful for diagnosing provider-level issues in isolation from the Blazor stack.

---

## Dependency Graph

```
FileManager.Tester
    └── FileManager.Providers
            └── FileManager

FileManager.Api
    ├── FileManager              (direct — required for static web assets)
    ├── FileManager.Providers
    └── FileManager.App
            └── FileManager.Providers.Api
                    └── FileManager
```

---

## NuGet Packages

| Package | Version | Install in |
|---|---|---|
| `FAST.FileManager` | 0.1.0 | Every project using the component or abstractions |
| `FAST.FileManager.Providers` | 0.1.0 | ASP.NET Core host projects (server-side only) |
| `FAST.FileManager.Providers.Api` | 0.1.0 | Blazor WASM client projects |

All three packages are versioned together and published simultaneously.

---

## Key Architectural Decisions

**No AWS SDK dependency.** The S3 client uses a hand-rolled SigV4 signer. This keeps the package footprint small and avoids dragging a large SDK into consuming projects.

**`IHttpClientFactory` over `new HttpClient()`.**  Direct `HttpClient` instantiation per provider causes socket exhaustion. All HTTP clients are registered through the factory.

**Static web assets require a direct project reference.** Blazor's asset pipeline does not propagate `_content/` assets through transitive project references. Any host project serving the component must directly reference `FileManager.csproj`.

**Providers never throw across the `IFileProvider` boundary.** Every method returns a `FileOperationResult`. Expected failures (not found, conflict, permission denied) are communicated as structured error codes, not exceptions.

**`CompositeProviderBuilder` is the authoritative wiring point.** When the S3 provider constructor changes, `CompositeProviderBuilder.cs` is the file to update — not `S3ProviderServiceCollectionExtensions.cs`.

---

## Roadmap

- File Open/Save dialogs
- Context menu
- `FAST.FileRepository` provider
- Cross-provider copy via stream pipe (no client-side download)
- Auth/credentials refactor
- Full NuGet publish (dry-run pipeline is in place)
