# FileManager

A dependency-free Blazor WASM file manager component and a transport-agnostic
file provider abstraction. Provider implementations (e.g. S3) ship as separate
packages.

## Solution status

This is an in-progress solution. Current contents:

| Project            | Status            | Description                                            |
|--------------------|-------------------|--------------------------------------------------------|
| `FileManager`      | Abstraction only  | NuGet package 1: provider abstraction + (later) the Blazor components. |

Planned, not yet present:

- `FileManager.Providers.S3` — NuGet package 2: hand-rolled S3 REST provider.
- The generic file manager WASM application.

## What is in `FileManager` today

The `FileManager.Abstractions` namespace — the transport-agnostic contract the
component and every provider compile against:

- `IFileProvider` — the provider interface (all async methods take a `CancellationToken`).
- `StructuredKey` — volume id + segmented path addressing.
- `StorageItem` — unified file/folder model.
- `Volume` — neutral top-level container (an S3 bucket maps to a volume).
- `Capabilities` — declares which operations a provider supports.
- `FileOperationResult` / `FileOperationResult<T>` — result types; providers do
  not throw across the boundary.
- `FileOperationError`, `StorageItemKind` — supporting enums.

The Blazor components belong in this same package and will be added later.

## Target

- .NET 9
- Blazor WebAssembly
- No third-party dependencies

## Build

```
dotnet build FileManager.sln
```
