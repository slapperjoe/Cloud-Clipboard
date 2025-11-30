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
   The build now creates an Azure Functions deployment package automatically:
   - `artifacts/CloudClipboard.Functions.zip` contains the publish output suitable for `az functionapp deployment source config-zip`.
   - The same zip is copied next to the Windows agent binary so the Deploy dialog can pick it up by default.
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

The binary lands under `src/WindowsAgent/CloudClipboard.Agent.Windows/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish`. Run `CloudClipboard.Agent.Windows.exe` directly and the agent will emit `agentsettings.json` in the same directory if it is missing. The publish folder will also carry the latest `CloudClipboard.Functions.zip`, so you can ship the agent + zip together for offline deployments.

## Deploying Azure Functions via the tray

- Prerequisites:
   - [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) installed and available on `PATH`.
   - An Azure Function App (`Consumption` or better) plus Storage account; the dialog only needs the app name, resource group, subscription, and Function key.
- Launch the agent, right-click the tray icon, and choose **Deploy Azure Functions…**.
- The dialog pre-fills the package path with the auto-generated zip. Update the Function App details and (optionally) the Functions key.
- Use **Sign In** to trigger `az login --use-device-code`. The log will show explicit errors if `az` cannot be found or authentication fails.
- Press **Deploy** to push the zip via `az functionapp deployment source config-zip`. Successful runs update `agentsettings.json` with the deployment metadata.
- All deployment settings (app name, resource group, subscription, package path) live in the Settings window under *Deployment Defaults* so you can edit them alongside the rest of the agent configuration.
- Recent enhancements:
   - The provisioning dialog now streams curated log lines (no more opaque `true`/JSON blobs) and records the HTTP status/body when seeding the owner configuration fails, so troubleshooting is faster.
   - Application Insights and Azure Web PubSub resources are created automatically inside the target resource group. Their connection strings are wired into the Function App settings, which keeps telemetry and realtime notifications working out-of-the-box.
   - Required Azure CLI extensions (`application-insights`, `webpubsub`) are detected and installed on-the-fly so first-time provisioning doesn’t stall.

## Notification delivery

Uploads fan out across two channels:

- **Azure Web PubSub (default)** – provisioning now creates a dedicated PubSub service inside the Function App resource group and injects its connection string into `Notifications:PubSub`. When the Windows agent has push enabled, it negotiates a WebSocket connection per owner and receives clipboard notifications instantly.
- **Long-poll fallback** – regardless of PubSub availability, the Functions backend still emits rows into `Storage:NotificationsTable`. Agents call `/api/clipboard/{ownerId}/notifications?timeoutSeconds=30` to keep a “server push” style HTTP loop alive. If PubSub is down (or the user declines to switch transports), the agent automatically falls back to this path.

Keep `EnablePushNotifications` set to `true` (default) in `agentsettings.json` to let the agent choose the best transport; set it to `false` if you prefer the slower history-refresh schedule only.

## Extensibility notes

- **MAUI clients** can reference `CloudClipboard.Core` to build UI surfaces for macOS/Linux/mobile without duplicating serialization logic.
- **Security**: add Azure AD auth in Functions, exchange for SAS tokens, and enable blob encryption scopes for production.
- **Windows shell integration**: register context-menu verbs that call a lightweight CLI which posts files into the agent queue (future work).
- **Automation hooks**: expose a lightweight CLI/Webhook so Power Automate, Logic Apps, or custom scripts can push clipboard entries without running the full Windows agent.

## Next steps

- Replace polling with clipboard hook APIs for better responsiveness.
- Harden Azure resources via IaC (Bicep/Terraform) and wire GitHub Actions for CI/CD.
- Introduce per-item encryption + user-level auth flows.
- Ship an installer/update channel (MSIX/Winget) so non-dev machines can pick up new agent builds safely.
- Add cross-platform agents (macOS/Linux) that reuse `CloudClipboard.Core` to round out the multi-device story.

---

```
   _________
       /  ____  /|
      /  / __/ / |
     /__/ /__/  |
     |  | |  |  |
     |  | |  |  |
     |  | |  |  |
     |  | |  |  |
     |  | |  |  |
     |  | |  |  |
     |  | |  | /
     |__|_|__|/
   ||
   ||
   ||
```

*ASCII doodle: a whimsical clipboard icon, purely decorative.*

> This project was built with plenty of help from GitHub Copilot (GPT-5.1-Codex Preview). Thanks for letting me pair! :)
