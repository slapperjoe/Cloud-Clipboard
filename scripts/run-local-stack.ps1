param(
    [int]$FunctionsPort = 7071,
    [switch]$SkipAgent = $false
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "[run-local-stack] $Message"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$functionsPath = Join-Path $repoRoot "src\Functions\CloudClipboard.Functions"
$agentPath = Join-Path $repoRoot "src\WindowsAgent\CloudClipboard.Agent.Windows"

if (-not (Test-Path $functionsPath)) {
    throw "Functions project path not found: $functionsPath"
}

if (-not (Test-Path $agentPath)) {
    throw "Windows agent project path not found: $agentPath"
}

if (-not (Get-Command func -ErrorAction SilentlyContinue)) {
    throw "Azure Functions Core Tools (func) not found in PATH. Install them before running this script."
}

Write-Step "Launching Azure Functions host on port $FunctionsPort in a new PowerShell window"
$funcCommand = "Set-Location -Path `"$functionsPath`"; func host start --port $FunctionsPort"
$funcProcess = Start-Process powershell -ArgumentList "-NoExit","-Command",$funcCommand -PassThru

$hostReady = $false
$maxAttempts = 20

Write-Step "Waiting for host readiness (up to $($maxAttempts * 2) seconds)"
for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    if ($funcProcess.HasExited) {
        throw "Functions host exited unexpectedly. Check the secondary PowerShell window for details."
    }
    Start-Sleep -Seconds 2
    try {
        $status = Invoke-RestMethod -Uri "http://localhost:$FunctionsPort/admin/host/status" -TimeoutSec 3
        if ($status.state -in @("Running","Initialized")) {
            $hostReady = $true
            break
        }
    } catch {
        # Host not ready yet; keep waiting
    }
    Write-Step "Host not ready yet (attempt $attempt/$maxAttempts)"
}

if (-not $hostReady) {
    $warning = "Functions host did not confirm readiness. Check the secondary PowerShell window for errors."
    Write-Warning $warning
    throw $warning
}

Write-Step "Functions host is responding."

if (-not $SkipAgent) {
    Write-Step "Starting Windows agent (Ctrl+C to stop)"
    Push-Location $agentPath
    try {
        dotnet run
    } finally {
        Pop-Location
    }

    Write-Step "Agent exited. Close the Functions host window when finished."
} else {
    Write-Step "SkipAgent flag set; leaving only the Functions host running."
}
