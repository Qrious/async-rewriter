#!/usr/bin/env bash
set -euo pipefail

# Setup script for running AsyncRewriter in Claude Code cloud sessions.
# Installs .NET 10 SDK and starts Neo4j via Docker.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"

echo "=== AsyncRewriter Cloud Environment Setup ==="

# --- .NET 10 SDK ---
if command -v dotnet &>/dev/null && dotnet --version 2>/dev/null | grep -q '^10\.'; then
    echo ".NET 10 SDK already installed: $(dotnet --version)"
else
    echo "Installing .NET 10 SDK..."
    # Try apt first (works on Ubuntu 24.04 cloud containers without sudo)
    if apt-get install -y --no-install-recommends dotnet-sdk-10.0 2>/dev/null; then
        echo ".NET 10 SDK installed via apt: $(dotnet --version)"
    else
        # Fallback to official install script
        curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
        chmod +x /tmp/dotnet-install.sh
        DOTNET_ROOT="$HOME/.dotnet"
        /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_ROOT"
        rm -f /tmp/dotnet-install.sh
        export DOTNET_ROOT
        export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
        if ! grep -q 'DOTNET_ROOT' "$HOME/.bashrc" 2>/dev/null; then
            {
                echo ""
                echo "# .NET SDK"
                echo "export DOTNET_ROOT=\"$DOTNET_ROOT\""
                echo "export PATH=\"\$DOTNET_ROOT:\$DOTNET_ROOT/tools:\$PATH\""
            } >> "$HOME/.bashrc"
        fi
        echo ".NET 10 SDK installed via script: $(dotnet --version)"
    fi
fi

# --- NuGet proxy helper ---
# .NET's HttpClient can fail to authenticate with proxies that use long JWT
# credentials (common in cloud sandbox environments). This local helper relays
# Proxy-Authorization headers properly to the upstream egress proxy.
PROXY_PID=""
if [ -n "${https_proxy:-${HTTPS_PROXY:-}}" ]; then
    echo "Starting NuGet proxy helper (working around .NET proxy auth issue)..."
    python3 "$SCRIPT_DIR/nuget-proxy-helper.py" &
    PROXY_PID=$!
    sleep 1

    if [ -f "$HOME/.nuget-proxy-port" ]; then
        LOCAL_PORT=$(cat "$HOME/.nuget-proxy-port")
        export http_proxy="http://127.0.0.1:$LOCAL_PORT"
        export https_proxy="http://127.0.0.1:$LOCAL_PORT"
        export HTTP_PROXY="http://127.0.0.1:$LOCAL_PORT"
        export HTTPS_PROXY="http://127.0.0.1:$LOCAL_PORT"
        echo "NuGet proxy helper running on port $LOCAL_PORT (PID $PROXY_PID)."
    else
        echo "Warning: proxy helper failed to start, NuGet restore may fail."
    fi
fi

# --- Neo4j (required for integration tests) ---
if docker ps --format '{{.Names}}' 2>/dev/null | grep -q neo4j; then
    echo "Neo4j container already running."
elif command -v docker &>/dev/null; then
    echo "Starting Neo4j via docker-compose..."
    docker compose -f "$REPO_DIR/docker-compose.yml" up neo4j -d 2>/dev/null \
        || docker-compose -f "$REPO_DIR/docker-compose.yml" up neo4j -d 2>/dev/null \
        || echo "Warning: could not start Neo4j. Integration tests may fail."
else
    echo "Warning: Docker not available. Neo4j not started; integration tests may fail."
fi

# --- Restore NuGet packages ---
echo "Restoring NuGet packages..."
dotnet restore "$REPO_DIR/AsyncRewriter.sln" --verbosity quiet

# Kill proxy helper now that packages are cached
if [ -n "$PROXY_PID" ]; then
    kill "$PROXY_PID" 2>/dev/null || true
    rm -f "$HOME/.nuget-proxy-port"
fi

echo "=== Setup complete ==="
echo "You can now run: dotnet build / dotnet test"
