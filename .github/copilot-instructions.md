# Cloud Clipboard – AI Agent Guide

## SDK & Prerequisites

- **SDK**: .NET 10 (pinned to `10.0.104` via `global.json`; `rollForward: latestFeature`)
- **Storage emulator**: [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite)
- **Functions Core Tools** (`func`) for local host
- **Azure CLI** (`az`) for deployment from the agent tray

## Project Layout

| Project | Description |
| --- | --- |
| `CloudClipboard.Core` | Shared models, serializers, and storage abstractions (`IClipboardMetadataStore`, `IClipboardPayloadStore`). In-memory test doubles live here too. |
| `CloudClipboard.Functions` | Azure Functions (isolated worker, v5) with HTTP triggers for upload, list, and download. |
| `CloudClipboard.Agent` | Cross-platform worker (Windows + Linux). Monitors the clipboard and syncs payloads to the Functions API. |

## Architecture Overview

```
[Windows Clipboard] → [Agent: ClipboardCaptureService]
                                ↓ (queues)
                         [Agent: ClipboardSyncWorker]
                                ↓ HTTP POST /api/clipboard/upload
                    [Functions: ClipboardFunctions]
                                ↓
                  [ClipboardCoordinator]
                   ↙                   ↘
       [TableClipboardMetadataStore]   [BlobClipboardPayloadStore]
```

Payloads flow: clipboard content → `ClipboardCaptureService` → `ClipboardSyncWorker` → `ClipboardFunctions.Upload` → `ClipboardCoordinator` → Azure Table (metadata) + Azure Blob (payload). Notifications fan out via Azure Web PubSub (WebSocket push) with a long-poll fallback through `/api/clipboard/{ownerId}/notifications`.

**Blob naming convention**: `{ownerId}/{yyyy/MM/dd}/{payloadtype}/{guid}.bin` — date-partitioned blobs per owner and payload type.

## Build & Test Commands

```bash
# Restore + build entire solution
dotnet restore CloudClipboard.sln
dotnet build CloudClipboard.sln

# Run ALL tests (xUnit)
dotnet test CloudClipboard.sln

# Run a single test by fully-qualified name
dotnet test --filter "FullyQualifiedName~SaveAsync_PersistsMetadataAndPayload"

# Run all tests in a specific project
dotnet test tests/CloudClipboard.Functions.Tests

# Publish self-contained Windows agent
dotnet publish src/WindowsAgent/CloudClipboard.Agent/CloudClipboard.Agent.csproj \
  -c Release -r win-x64 --self-contained true \
  /p:PublishSingleFile=true /p:PublishTrimmed=false

# Start local stack (Functions host only)
pwsh -ExecutionPolicy Bypass -File ./scripts/run-local-stack.ps1 -SkipAgent
```

**Auto-versioning**: Each Agent build increments `build/Version.props` via the `VersionIncrementer` tool. The `Directory.Build.props` file propagates `CloudClipboardVersion` to `Version`, `FileVersion`, and `AssemblyVersion`.

**Auto-zip**: Building `CloudClipboard.Functions` automatically creates `artifacts/CloudClipboard.Functions.zip` (publish output) for deployments. The Agent build also copies this zip alongside the agent binary.

## Key Conventions

- **Target framework**: `net10.0` (all projects)
- **Nullable**: `enable` in every project
- **ImplicitUsings**: `enable` in every project
- **Dependency Injection**: Used throughout — `Program.cs` in Functions and Agent both wire up `IClipboardMetadataStore`, `IClipboardPayloadStore`, `ClipboardPayloadSerializer`, and `ClipboardCoordinator` as singletons.
- **TimeProvider** abstraction: `ClipboardCoordinator` accepts a `TimeProvider?` (defaults to `TimeProvider.System`) so tests can inject `FixedTimeProvider` for deterministic timestamps.
- **Payload pipeline**: `ClipboardPayloadDescriptor` + `ClipboardPayloadPart` are records (immutable) — always pass through the serializer, never serialize raw bytes directly.
- **Storage abstractions**: Code to interfaces in `CloudClipboard.Core.Abstractions`. Implementations (`BlobClipboardPayloadStore`, `TableClipboardMetadataStore`) live in the Functions project. In-memory doubles (`InMemoryClipboardPayloadStore`, `InMemoryClipboardMetadataStore`) live in Core for tests.
- **Agent platform gate**: `UseWindowsForms` is conditionally enabled only on Windows (`$(OS) == 'Windows_NT'`); a `WINDOWS` define constant gates Windows-only code paths. Linux uses `LinuxClipboardAccess` (xclip/wl-copy) and `LinuxTrayIcon` (D-Bus StatusNotifier). Runtime detection (`PlatformDetector`) wires the correct implementations in `Program.cs`.
- **Functions auth**: Function-level keys via `x-functions-key` header; future Entra ID support planned.
- **Notifications**: Azure Web PubSub (WebSocket) is the default push channel. Long-poll fallback (`/api/clipboard/{ownerId}/notifications?timeoutSeconds=30`) keeps agents updated even when PubSub is unavailable.

## Linux Build

The agent supports Linux. `Program.cs` detects the platform at runtime and wires up Linux-specific services:

- **Clipboard access**: `LinuxClipboardAccess` uses `wl-copy`/`wl-paste` (Wayland) or `xclip` (X11) via `Process.Start`.
- **Tray icon**: `LinuxTrayIcon` implements the freedesktop.org `StatusNotifierItem` D-Bus spec via `Tmds.DBus`.
- **Platform detection**: `PlatformDetector.IsLinux` gates the Linux path.

Linux requires `xclip` or `wl-clipboard` binaries on PATH. Unlike Windows, Linux doesn't need Windows Forms — the csproj conditionally sets `UseWindowsForms` only on Windows.
