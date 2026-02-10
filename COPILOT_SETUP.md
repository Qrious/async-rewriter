# Copilot Setup Instructions

This guide will help you set up your development environment for working with the Async Rewriter project, including .NET 10 SDK installation and AI coding assistant configuration.

## Prerequisites

### 1. Install .NET 10 SDK

The Async Rewriter project requires .NET 10.0 SDK.

#### Windows

1. Download the .NET 10 SDK from the official Microsoft website:
   - Visit: https://dotnet.microsoft.com/download/dotnet/10.0
   - Download the SDK installer for Windows
   - Run the installer and follow the prompts

2. Verify installation:
   ```powershell
   dotnet --version
   ```
   You should see `10.0.x` (e.g., `10.0.0`)

#### macOS

1. **Option A: Using the installer**
   - Visit: https://dotnet.microsoft.com/download/dotnet/10.0
   - Download the SDK installer for macOS (choose ARM64 for Apple Silicon or x64 for Intel Macs)
   - Open the downloaded .pkg file and follow the installation prompts

2. **Option B: Using Homebrew**
   ```bash
   brew install --cask dotnet-sdk
   ```

3. Verify installation:
   ```bash
   dotnet --version
   ```

#### Linux (Ubuntu/Debian)

1. Add the Microsoft package repository:
   ```bash
   wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
   sudo dpkg -i packages-microsoft-prod.deb
   rm packages-microsoft-prod.deb
   ```

2. Install the .NET 10 SDK:
   ```bash
   sudo apt-get update
   sudo apt-get install -y dotnet-sdk-10.0
   ```

3. Verify installation:
   ```bash
   dotnet --version
   ```

#### Linux (Fedora/RHEL/CentOS)

1. Add the Microsoft package repository:
   ```bash
   sudo dnf install -y dotnet-sdk-10.0
   ```

2. Verify installation:
   ```bash
   dotnet --version
   ```

### 2. Install Docker and Docker Compose

Neo4j is required for the Async Rewriter server and runs via Docker.

#### Windows

1. Download and install Docker Desktop from: https://www.docker.com/products/docker-desktop
2. Docker Compose is included with Docker Desktop

#### macOS

1. Download and install Docker Desktop from: https://www.docker.com/products/docker-desktop
2. Docker Compose is included with Docker Desktop

#### Linux

1. Install Docker:
   ```bash
   # Ubuntu/Debian
   sudo apt-get update
   sudo apt-get install -y docker.io docker-compose
   
   # Fedora
   sudo dnf install -y docker docker-compose
   ```

2. Start and enable Docker service:
   ```bash
   sudo systemctl start docker
   sudo systemctl enable docker
   ```

3. Add your user to the docker group (optional, to run docker without sudo):
   ```bash
   sudo usermod -aG docker $USER
   # Log out and log back in for this to take effect
   ```

4. Verify installation:
   ```bash
   docker --version
   docker-compose --version
   ```

### 3. Install Git

If you don't have Git installed:

- **Windows**: Download from https://git-scm.com/download/win
- **macOS**: `brew install git` or download from https://git-scm.com/download/mac
- **Linux**: `sudo apt-get install git` (Ubuntu/Debian) or `sudo dnf install git` (Fedora)

## IDE Setup

### Visual Studio Code (Recommended)

1. **Install VS Code**: Download from https://code.visualstudio.com/

2. **Install recommended extensions**:
   - C# (Microsoft) - `ms-dotnettools.csharp`
   - C# Dev Kit (Microsoft) - `ms-dotnettools.csdevkit`
   - GitHub Copilot - `GitHub.copilot`
   - GitHub Copilot Chat - `GitHub.copilot-chat`
   - Docker - `ms-azuretools.vscode-docker`

3. **Open the project**:
   ```bash
   code /path/to/async-rewriter
   ```

### Visual Studio 2022

1. **Install Visual Studio 2022** (version 17.8 or later for .NET 10 support)
   - Download from: https://visualstudio.microsoft.com/downloads/
   - During installation, select the ".NET desktop development" workload

2. **Install GitHub Copilot**:
   - Go to Extensions → Manage Extensions
   - Search for "GitHub Copilot"
   - Install and restart Visual Studio

3. **Open the solution**:
   - Open `AsyncRewriter.sln` in Visual Studio

### JetBrains Rider

1. **Install Rider**: Download from https://www.jetbrains.com/rider/

2. **Install GitHub Copilot plugin**:
   - Go to Settings → Plugins
   - Search for "GitHub Copilot"
   - Install and restart Rider

3. **Open the solution**:
   - Open `AsyncRewriter.sln` in Rider

## GitHub Copilot Setup

### 1. Sign up for GitHub Copilot

- Visit: https://github.com/features/copilot
- Sign up for an individual subscription or request access through your organization
- Follow the setup instructions for your IDE

### 2. Authenticate GitHub Copilot

#### In VS Code:
1. Click on the GitHub Copilot icon in the status bar
2. Follow the prompts to sign in with your GitHub account
3. Authorize GitHub Copilot when prompted

#### In Visual Studio or Rider:
1. Sign in with your GitHub account when prompted
2. Accept the authorization request

### 3. Test GitHub Copilot

Create a new C# file and start typing a comment like:
```csharp
// Function to calculate factorial
```

Copilot should suggest a function implementation. Press `Tab` to accept the suggestion.

## Repository Setup

### 1. Clone the Repository

```bash
git clone https://github.com/Qrious/async-rewriter.git
cd async-rewriter
```

### 2. Restore Dependencies

```bash
dotnet restore
```

### 3. Build the Solution

```bash
dotnet build
```

### 4. Run Tests

```bash
dotnet test
```

## Running the Application

### 1. Start Neo4j

Using Docker Compose (recommended):
```bash
docker compose up neo4j -d
# or for older Docker versions: docker-compose up neo4j -d
```

Wait a few seconds for Neo4j to fully start, then verify it's running:
```bash
docker compose ps
# or for older Docker versions: docker-compose ps
```

Access Neo4j Browser at http://localhost:7474
- Username: `neo4j`
- Password: `password`

### 2. Start the Server

```bash
cd src/AsyncRewriter.Server
dotnet run
```

The API will be available at `http://localhost:5000`
Swagger documentation: `http://localhost:5000/swagger`

### 3. Use the CLI Client

In a new terminal:
```bash
cd src/AsyncRewriter.Client
dotnet run -- analyze /path/to/your/project.csproj
```

## AI Coding Assistant Tips

### Using GitHub Copilot with This Project

1. **CLAUDE.md file**: This repository includes a `CLAUDE.md` file that provides context to Claude Code (claude.ai/code) about the project structure, build commands, and architecture. This helps AI assistants understand the codebase better.

2. **Leverage inline suggestions**: As you write code, Copilot will suggest completions. Use:
   - `Tab` to accept a suggestion
   - `Esc` to dismiss a suggestion
   - `Alt+]` (Windows/Linux) or `Option+]` (macOS) to see next suggestion
   - `Alt+[` (Windows/Linux) or `Option+[` (macOS) to see previous suggestion

3. **Use Copilot Chat**: Ask questions about the code:
   - "Explain how the async flooding algorithm works"
   - "How do I add a new endpoint to the API?"
   - "What does the CallGraphAnalyzer do?"

4. **Code generation**: Write descriptive comments for complex logic:
   ```csharp
   // Traverse the call graph using BFS starting from root methods
   // and mark all caller methods as requiring async transformation
   ```

5. **Test generation**: Copilot can help generate unit tests:
   ```csharp
   // Unit test for AsyncFloodingAnalyzer with multiple root methods
   ```

### Best Practices

1. **Review AI-generated code**: Always review and test code suggested by AI assistants
2. **Understand the suggestions**: Make sure you understand what the suggested code does
3. **Follow project conventions**: Ensure AI-generated code follows the project's coding style (see CLAUDE.md for conventions)
4. **Run tests**: Always run `dotnet test` after making changes
5. **Use the existing patterns**: Look at similar code in the project as examples

## Verifying Your Setup

Run through this checklist to verify everything is set up correctly:

### Check .NET Version
```bash
dotnet --version
# Should output: 10.0.x
```

### Check Docker
```bash
docker --version
docker-compose --version
```

### Build the Project
```bash
cd /path/to/async-rewriter
dotnet restore
dotnet build
```
Expected output: `Build succeeded. 0 Warning(s), 0 Error(s)`

### Run Tests
```bash
dotnet test
```
Expected output: All tests should pass

### Start Neo4j
```bash
docker-compose up neo4j -d
docker-compose ps
```
Expected output: Neo4j container should be running

### Access Neo4j Browser
Open http://localhost:7474 in your browser
- Login with username `neo4j` and password `password`

### Start the Server
```bash
dotnet run --project src/AsyncRewriter.Server
```
Expected output: Server should start and be listening on http://localhost:5000

### Test the API
Open http://localhost:5000/swagger in your browser
- You should see the Swagger UI with API endpoints

## Troubleshooting

### .NET SDK Issues

**Problem**: `dotnet: command not found`
- **Solution**: Make sure .NET SDK is installed and in your PATH. On Windows, restart your terminal/IDE after installation.

**Problem**: Wrong .NET version installed
- **Solution**: Run `dotnet --list-sdks` to see all installed SDKs. Install .NET 10 SDK if missing.

### Docker Issues

**Problem**: `Cannot connect to the Docker daemon`
- **Solution**: Make sure Docker Desktop is running (Windows/macOS) or the Docker service is started (Linux: `sudo systemctl start docker`)

**Problem**: Port 7474 or 7687 already in use
- **Solution**: Stop any existing Neo4j instances or change the port mappings in `docker-compose.yml`

### Neo4j Connection Issues

**Problem**: Server cannot connect to Neo4j
- **Solution**: 
  1. Verify Neo4j is running: `docker-compose ps`
  2. Check the connection settings in `src/AsyncRewriter.Server/appsettings.json`
  3. Try restarting Neo4j: `docker-compose restart neo4j`

### Build Errors

**Problem**: `The type or namespace name '...' could not be found`
- **Solution**: Run `dotnet restore` to restore NuGet packages

**Problem**: Build fails with Roslyn-related errors
- **Solution**: Clean and rebuild: `dotnet clean && dotnet build`

### GitHub Copilot Issues

**Problem**: Copilot not providing suggestions
- **Solution**: 
  1. Check that you're signed in to GitHub in your IDE
  2. Verify your GitHub Copilot subscription is active
  3. Try reloading the window (VS Code: Ctrl+Shift+P → "Reload Window")

**Problem**: Copilot suggestions not relevant
- **Solution**: 
  1. Provide more context in comments
  2. Open related files so Copilot has more context
  3. Review the CLAUDE.md file for project-specific patterns

## Additional Resources

- [.NET 10 Documentation](https://docs.microsoft.com/en-us/dotnet/)
- [GitHub Copilot Documentation](https://docs.github.com/en/copilot)
- [Docker Documentation](https://docs.docker.com/)
- [Neo4j Documentation](https://neo4j.com/docs/)
- [Roslyn Documentation](https://docs.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/)
- [Project README](README.md)
- [Project CLAUDE.md](CLAUDE.md)

## Getting Help

If you encounter issues not covered in this guide:

1. Check the [project README](README.md) for additional documentation
2. Review the [CLAUDE.md](CLAUDE.md) file for project architecture details
3. Check existing GitHub issues: https://github.com/Qrious/async-rewriter/issues
4. Create a new issue with details about your problem

## Contributing

Once your environment is set up, see the project README for contribution guidelines and workflow examples.
