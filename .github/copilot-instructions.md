## Cloud Clipboard – AI Agent Guide

**Architecture Summary** – Three .NET 8 projects: `CloudClipboard.Core` (shared models, payload serialization, storage abstractions), `CloudClipboard.Functions` (HTTP Azure Functions running on the isolated worker that accept uploads, list/download clipboard items, and persist blobs/tables), and `CloudClipboard.Agent.Windows` (Windows-only worker that monitors the OS clipboard, builds payload descriptors, and POSTs to the Functions API). Payloads flow: Windows clipboard → agent → HTTP `clipboard/upload` → `ClipboardCoordinator` → Azure Blob/Table storage.

**Key Files & Directories**
- `src/Core/CloudClipboard.Core/` – Domain models (`ClipboardPayloadDescriptor`, `ClipboardItemMetadata`), `ClipboardPayloadSerializer`, storage interfaces + in-memory test doubles.
- `src/Functions/CloudClipboard.Functions/Functions/ClipboardFunctions.cs` – HTTP triggers for upload/list/download that call `ClipboardCoordinator`.
- `src/Functions/CloudClipboard.Functions/Storage/` – Azure Blob/Table implementations of the storage abstractions.
- `src/WindowsAgent/CloudClipboard.Agent.Windows/Services/ClipboardCaptureService.cs` – Windows clipboard poller that turns text/files/images into descriptors.
- `src/WindowsAgent/CloudClipboard.Agent.Windows/Services/ClipboardSyncWorker.cs` – Reads queued captures and dispatches them via `HttpCloudClipboardClient`.
- `README.md` – Dev workflow, prerequisites (Azurite + Functions Core Tools), and run commands.

**Build & Test Commands**
- Restore/build everything: `dotnet restore CloudClipboard.sln` then `dotnet build CloudClipboard.sln` (requires .NET 8 SDK + System.Data.Common; repair SDK if missing).
- Run Azure Functions locally: `cd src/Functions/CloudClipboard.Functions` then `func start` (needs Azurite or Azure Storage).
- Run the Windows agent (interactive console): `cd src/WindowsAgent/CloudClipboard.Agent.Windows` then `dotnet run` after configuring `appsettings.json` (`Agent.OwnerId`, `ApiBaseUrl`, `FunctionKey`).

**Project Conventions & Patterns**
- Everything targets .NET 8; shared logic lives in `CloudClipboard.Core` so future MAUI apps reuse serialization/storage contracts.
- Dependency Injection everywhere (Options pattern for config, `ClipboardPayloadSerializer` registered as singleton, hosted services for background loops).
- Clipboard payload handling always goes through `ClipboardPayloadDescriptor` + `ClipboardPayloadPart` so features (images/file sets/text) stay consistent across agent + backend.
- Azure Functions stick to isolated worker defaults, returning DTOs via `HttpResponseData.WriteAsJsonAsync` and logging with `ILogger`.

**Integration Points**
- Azure Storage: configure `Storage:BlobConnectionString`, `Storage:TableConnectionString`, `PayloadContainer`, `MetadataTable` in `local.settings.json` (Functions). Defaults use Azurite (`UseDevelopmentStorage=true`).
- Agent auth: Functions key provided via `Agent.FunctionKey` header `x-functions-key`; future Entra ID can replace this.
- HTTP endpoints: `/api/clipboard/upload`, `/api/clipboard/{ownerId}`, `/api/clipboard/{ownerId}/{itemId}`.
- Windows clipboard access relies on STA threads via `ClipboardCaptureService`; keep `UseWindowsForms` + `System.Drawing.Common` references intact.

**Examples / Code Patterns**
- Creating a payload descriptor from bytes (used by agent capture):
  ```csharp
  var descriptor = new ClipboardPayloadDescriptor(
      ClipboardPayloadType.Image,
      new[] { new ClipboardPayloadPart("clipboard.png", "image/png", buffer.LongLength, () => new MemoryStream(buffer, false)) },
      preferredContentType: "image/png");
  ```
- Wiring Functions dependencies (see `Program.cs`):
  ```csharp
  services.AddSingleton<IClipboardMetadataStore, TableClipboardMetadataStore>();
  services.AddSingleton<IClipboardPayloadStore, BlobClipboardPayloadStore>();
  services.AddSingleton<ClipboardPayloadSerializer>();
  services.AddSingleton<ClipboardCoordinator>();
  ```
- Enqueueing clipboard snapshots inside the agent:
  ```csharp
  if (snapshot is not null && snapshot.Signature != _lastSignature)
  {
      _lastSignature = snapshot.Signature;
      await _queue.EnqueueAsync(snapshot.Request, stoppingToken);
  }
  ```
