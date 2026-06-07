# Cloud Clipboard – AI Agent Guide

## SDK & Prerequisites

- **SDK**: .NET 10 (pinned to `10.0.104` via `global.json`; `rollForward: latestFeature`)
- **SDK location**: Installed locally at `~/.dotnet`. Add to PATH before use: `export PATH="$HOME/.dotnet:$PATH"`
- **Storage emulator**: [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite)
- **Functions Core Tools** (`func`) for local host
- **Azure CLI** (`az`) for deployment from the agent tray

## Project Layout

| Project | Description |
| --- | --- |
| `CloudClipboard.Core` | Shared models, serializers, and storage abstractions (`IClipboardMetadataStore`, `IClipboardPayloadStore`). In-memory test doubles live here too. |
| `CloudClipboard.Functions` | Azure Functions (isolated worker, v4) with HTTP triggers for upload, list, download, notifications, and owner configuration. |
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

**Agent hosted services** (all `IHostedService`, registered in `Program.cs`):
| Service | Role |
| --- | --- |
| `ClipboardCaptureService` | Polls clipboard every N seconds, detects changes via SHA-256 hash |
| `ClipboardSyncWorker` | Dequeues and uploads via `ICloudClipboardClient`, with exponential backoff |
| `ClipboardHistoryWorker` | Periodically refreshes clipboard history from the API |
| `OwnerStateWorker` | Syncs owner pause/resume state |
| `ClipboardPushListener` | WebSocket connection to PubSub for real-time notifications; falls back to polling |
| `TrayIconHostedService` | System tray icon (Windows Forms on Windows, Avalonia D-Bus on Linux) |
| `FirstRunConfigurationService` | Guides first-run setup wizard |
| `OwnerConfigurationSyncService` | Fetches cloud-side owner config on startup |

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
dotnet test tests/CloudClipboard.Agent.Tests

# Publish self-contained Windows agent
dotnet publish src/WindowsAgent/CloudClipboard.Agent/CloudClipboard.Agent.csproj \
  -c Release -r win-x64 --self-contained true \
  /p:PublishSingleFile=true /p:PublishTrimmed=false

# Start local stack (Functions host + agent)
pwsh -ExecutionPolicy Bypass -File ./scripts/run-local-stack.ps1
# Or with Bash (Linux):
bash ./scripts/run-local-stack.sh
# Both support --skip-agent to only boot the Functions host
```

**Auto-versioning**: Each Agent build increments `build/Version.props` via the `VersionIncrementer` tool. The `Directory.Build.props` file propagates `CloudClipboardVersion` to `Version`, `FileVersion`, and `AssemblyVersion`.

**Auto-zip**: Building `CloudClipboard.Functions` automatically creates `artifacts/CloudClipboard.Functions.zip` (publish output) for deployments. The Agent build also copies this zip alongside the agent binary.

## Key Conventions

- **Target framework**: `net10.0` (all projects)
- **Nullable**: `enable` in every project
- **ImplicitUsings**: `enable` in every project
- **File-scoped namespaces**: All source files use `namespace X;` (no block-scoped namespaces).
- **Dependency Injection**: Used throughout — `Program.cs` in Functions and Agent both wire up `IClipboardMetadataStore`, `IClipboardPayloadStore`, `ClipboardPayloadSerializer`, and `ClipboardCoordinator` as singletons. The Agent also registers `ICloudClipboardClient` using a decorator pattern (`SetupAwareCloudClipboardClient` wraps `HttpCloudClipboardClient` via `ActivatorUtilities.CreateInstance`).
- **TimeProvider** abstraction: `ClipboardCoordinator` accepts a `TimeProvider?` (defaults to `TimeProvider.System`) so tests can inject a fixed-time provider for deterministic timestamps. Tests define their own `FixedTimeProvider : TimeProvider` inline — there is no shared test utility for this.
- **Payload pipeline**: `ClipboardPayloadDescriptor` + `ClipboardPayloadPart` are records (immutable) — always pass through the serializer, never serialize raw bytes directly.
- **Records everywhere**: All models (`ClipboardItemMetadata`, `ClipboardPayloadDescriptor`, `ClipboardPayloadPart`, `ClipboardUploadRequest`, `ClipboardOwnerState`, `SerializedClipboardPayload`) are `record` types.
- **Storage abstractions**: Code to interfaces in `CloudClipboard.Core.Abstractions`. Implementations (`BlobClipboardPayloadStore`, `TableClipboardMetadataStore`, `TableOwnerStateStore`) live in the Functions project. In-memory doubles (`InMemoryClipboardPayloadStore`, `InMemoryClipboardMetadataStore`) live in Core for tests.
- **Agent platform gate**: `UseWindowsForms` is conditionally enabled only on Windows (`$(OS) == 'Windows_NT'`); a `WINDOWS` define constant gates Windows-only code paths. Linux uses `LinuxClipboardAccess` (xclip/wl-copy) and `LinuxTrayIcon` (D-Bus StatusNotifier). `PlatformDetector` (runtime via `RuntimeInformation`) wires the correct implementations in `Program.cs`.
- **`IClipboardAccess` interface**: Abstracts clipboard operations (read/write text, image, files). `WindowsClipboardAccess` and `LinuxClipboardAccess` implement it. The `ClipboardCaptureService` and `ClipboardPasteService` depend on this interface.
- **Functions auth**: Function-level keys via `x-functions-key` header (appended as `?code=` query param by the Agent's `HttpCloudClipboardClient`).
- **Notifications**: Two channels — `WebPubSubNotificationService` (WebSocket push via Azure Web PubSub) and `TableClipboardNotificationService` (long-poll via `/api/clipboard/{ownerId}/notifications`). The Agent's `ClipboardPushListener` tries PubSub first, falls back to polling.
- **Agent logging**: Writes to a file (`agent.log` in the settings directory) via a custom `FileLoggerProvider`/`FileLogger`. No console output.
- **Agent single-instance**: Uses a named mutex (`Global\CloudClipboardAgent` on Windows, `CloudClipboardAgent` on Linux) to prevent duplicate instances.
- **Agent configuration**: Settings stored in `agentsettings.json` at `AgentSettingsPathProvider.GetSettingsPath()`. Backed up on startup via `AgentSettingsBackup.TryCreateBackup`.
- **DTO separation**: Function-side DTOs live in `CloudClipboard.Functions/Dtos/`. The Agent defines its own request/response records inline in `HttpCloudClipboardClient` (e.g., `UploadEnvelope`, `StateEnvelope`). The `ClipboardItemDto` bridges both sides.
- **Functions test isolation**: `CloudClipboard.Functions.Tests` does NOT reference the Functions project — it only references Core. Tests use `InMemoryClipboardMetadataStore` and `InMemoryClipboardPayloadStore` to test `ClipboardCoordinator` logic without Azure dependencies.

## Linux Build

The agent supports Linux. `Program.cs` detects the platform at runtime and wires up Linux-specific services:

- **Clipboard access**: `LinuxClipboardAccess` tries `wl-copy`/`wl-paste` (Wayland) first, then `xclip` (X11) as fallback, via `Process.Start`.
- **Tray icon**: `LinuxTrayIcon` implements the freedesktop.org `StatusNotifierItem` D-Bus spec via `Tmds.DBus`.
- **GUI bootstrap**: On Linux, the host runs in a background task, then a minimal Avalonia window (1×1px, Skia+X11 backend) is created to keep the app alive as a GUI process with a tray icon.
- **Platform detection**: `PlatformDetector.IsLinux` gates the Linux path. `PlatformDetector.IsWindows` gates Windows paths.

Linux requires `xclip` or `wl-clipboard` binaries on PATH. `UseWindowsForms` is conditionally set to `true` only on Windows. `InvariantGlobalization` is set to `true` in the Agent csproj.
