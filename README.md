# Cloud Clipboard MVP

A .NET 8 solution that synchronizes clipboard content (text, files, and images) through Azure Blob Storage + Azure Functions. The initial scope targets Windows, but projects are structured so a .NET MAUI client can reuse the shared `CloudClipboard.Core` library later.

## Project layout

| Project | Description |
| --- | --- |
| `CloudClipboard.Core` | Shared models (`ClipboardPayloadDescriptor`, `ClipboardItemMetadata`), payload serialization helpers, and abstraction interfaces for storage. |
| `CloudClipboard.Functions` | Azure Functions (HTTP, .NET isolated) that accept uploads, list history, and stream clipboard items using Blob Storage + Table Storage. |
| `CloudClipboard.Agent.Windows` | Windows worker/tray-ready agent that watches the local clipboard, packages payloads, and uploads them through the Functions API. |

## Local development workflow

1. **Azure storage emulator**: run [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite). The Functions project's `local.settings.json` defaults to `UseDevelopmentStorage=true`.
2. **Restore & build**:
   ```powershell
   dotnet restore CloudClipboard.sln
   dotnet build CloudClipboard.sln
   ```
   If your SDK install is missing `System.Data.Common`, install the .NET 8 Windows Hosting bundle or repair the SDK.
3. **Start the Functions backend**:
   ```powershell
   cd src/Functions/CloudClipboard.Functions
   func start
   ```
4. **Run the Windows agent (interactive console)**:
   ```powershell
   cd src/WindowsAgent/CloudClipboard.Agent.Windows
   dotnet run
   ```
   Update `appsettings.json` (OwnerId, DeviceName, FunctionKey) before running. The agent polls the Windows clipboard every 3 seconds and uploads changes. When the tray icon starts, it creates an `agentsettings.json` file next to the executable so portable builds can travel with their configuration.

   > Tip: Use the tray icon to switch between **Automatic** and **Manual** upload modes. Manual mode stages captures locally until you trigger *Upload Cached Clipboard* from the tray menu or press the configurable hotkey (defaults to `Ctrl+Shift+U`). You can also pull the most recent cloud clipboard item on demand via *Download Latest Clipboard* or the `Ctrl+Shift+D` hotkey. When you need to start fresh, choose *Wipe Cloud Storage…* to delete every stored clipboard entry for the current owner. Enable real-time push (default) to let the agent long-poll the Functions notifications endpoint so uploads show up almost instantly.

   Need both pieces running together? Use the helper script to launch the Functions host in a separate window and, optionally, the agent once the host reports ready:

   ```powershell
   # From repo root
   pwsh -ExecutionPolicy Bypass -File ./scripts/run-local-stack.ps1
   # Add -SkipAgent to only boot the Functions host; use -FunctionsPort to change the port
   ```
   The script pings `http://localhost:<port>/admin/host/status` until it sees the host in `Running` state, so you get immediate feedback if Core Tools fails.

## Portable Windows agent build

Publish a single self-contained executable that you can copy to any Windows x64 machine (no shared framework required):

```powershell
dotnet publish src/WindowsAgent/CloudClipboard.Agent.Windows/CloudClipboard.Agent.Windows.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  /p:PublishTrimmed=false
```

The binary lands under `src/WindowsAgent/CloudClipboard.Agent.Windows/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish`. Run `CloudClipboard.Agent.Windows.exe` directly and the agent will emit `agentsettings.json` in the same directory if it is missing.

## Notification delivery

The Functions backend now emits notification rows into the `Storage:NotificationsTable` table every time an upload succeeds. Agents call `/api/clipboard/{ownerId}/notifications?timeoutSeconds=30` using a long-poll request so the server can hold the HTTP connection open until either a notification is available or the timeout elapses. No extra Azure resources are required—Azurite or a standard storage account is enough. Keep `EnablePushNotifications` set to `true` (default) in `agentsettings.json` to enable the background poller; set it to `false` if you prefer the agent to refresh history on its slower polling schedule only.

## Extensibility notes

- **MAUI clients** can reference `CloudClipboard.Core` to build UI surfaces for macOS/Linux/mobile without duplicating serialization logic.
- **Security**: add Azure AD auth in Functions, exchange for SAS tokens, and enable blob encryption scopes for production.
- **Windows shell integration**: register context-menu verbs that call a lightweight CLI which posts files into the agent queue (future work).
- **Notifications**: swap the Table Storage long-poll mechanism with Azure SignalR (or another managed push service) if you later need global fan-out and connection management.

## Next steps

- Replace polling with clipboard hook APIs for better responsiveness.
- Add download/paste workflow in the agent (list recent items, choose destination, push into local clipboard).
- Harden Azure resources via IaC (Bicep/Terraform) and wire GitHub Actions for CI/CD.
- Introduce per-item encryption + user-level auth flows.
