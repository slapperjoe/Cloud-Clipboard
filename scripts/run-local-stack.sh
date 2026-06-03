#!/usr/bin/env bash
set -euo pipefail

# Defaults
FUNCTIONS_PORT=${FUNCTIONS_PORT:-7071}
SKIP_AGENT=${SKIP_AGENT:-false}

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --port)
            FUNCTIONS_PORT="$2"; shift 2;;
        --skip-agent)
            SKIP_AGENT="true"; shift;;
        --help|-h)
            echo "Usage: $0 [--port PORT] [--skip-agent]"
            echo "  --port PORT      Functions host port (default: 7071)"
            echo "  --skip-agent     Skip starting the agent, only start Functions host"
            exit 0;;
        *)
            echo "Unknown option: $1" >&2; exit 1;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

FUNCTIONS_PATH="$REPO_ROOT/src/Functions/CloudClipboard.Functions"
AGENT_PATH="$REPO_ROOT/src/WindowsAgent/CloudClipboard.Agent"

log() {
    echo "[run-local-stack] $*"
}

# Validate
if [[ ! -d "$FUNCTIONS_PATH" ]]; then
    echo "ERROR: Functions project path not found: $FUNCTIONS_PATH" >&2
    exit 1
fi

if [[ ! -d "$AGENT_PATH" ]]; then
    echo "ERROR: Agent project path not found: $AGENT_PATH" >&2
    exit 1
fi

if ! command -v func &>/dev/null; then
    echo "ERROR: Azure Functions Core Tools (func) not found in PATH." >&2
    exit 1
fi

export PATH="$HOME/.dotnet:$PATH"

# Start Functions host in background
log "Launching Azure Functions host on port $FUNCTIONS_PORT (background)"
pushd "$FUNCTIONS_PATH" >/dev/null
func host start --port "$FUNCTIONS_PORT" &
FUNC_PID=$!
popd >/dev/null

# Wait for readiness
HOST_READY=false
MAX_ATTEMPTS=20
log "Waiting for host readiness (up to $((MAX_ATTEMPTS * 2)) seconds)"

for ((attempt=1; attempt<=MAX_ATTEMPTS; attempt++)); do
    if ! kill -0 "$FUNC_PID" 2>/dev/null; then
        log "Functions host exited unexpectedly (PID: $FUNC_PID)"
        exit 1
    fi

    sleep 2

    STATE=$(curl -s --max-time 3 "http://localhost:${FUNCTIONS_PORT}/admin/host/status" 2>/dev/null | jq -r '.state' 2>/dev/null || true)

    if [[ "$STATE" == "Running" || "$STATE" == "Initialized" ]]; then
        HOST_READY=true
        break
    fi

    log "Host not ready yet (attempt $attempt/$MAX_ATTEMPTS)"
done

if [[ "$HOST_READY" != "true" ]]; then
    log "WARNING: Functions host did not confirm readiness."
    kill "$FUNC_PID" 2>/dev/null || true
    exit 1
fi

log "Functions host is responding on port $FUNCTIONS_PORT (PID: $FUNC_PID)"

if [[ "$SKIP_AGENT" != "true" ]]; then
    log "Starting Agent (Ctrl+C to stop)"
    export PATH="$HOME/.dotnet:$PATH"
    pushd "$AGENT_PATH" >/dev/null
    dotnet run
    AGENT_EXIT=$?
    popd >/dev/null

    log "Agent exited (code: $AGENT_EXIT). Functions host still running (PID: $FUNC_PID)."
    log "Run 'kill $FUNC_PID' to stop it."
else
    log "SkipAgent flag set; leaving only the Functions host running (PID: $FUNC_PID)."
    log "Run 'kill $FUNC_PID' to stop it."
fi
