FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["AsyncRewriter.sln", "./"]
COPY ["src/AsyncRewriter.Core/AsyncRewriter.Core.csproj", "src/AsyncRewriter.Core/"]
COPY ["src/AsyncRewriter.Analyzer/AsyncRewriter.Analyzer.csproj", "src/AsyncRewriter.Analyzer/"]
COPY ["src/AsyncRewriter.Neo4j/AsyncRewriter.Neo4j.csproj", "src/AsyncRewriter.Neo4j/"]
COPY ["src/AsyncRewriter.Transformation/AsyncRewriter.Transformation.csproj", "src/AsyncRewriter.Transformation/"]
COPY ["src/AsyncRewriter.Server/AsyncRewriter.Server.csproj", "src/AsyncRewriter.Server/"]

# Restore dependencies
RUN dotnet restore "src/AsyncRewriter.Server/AsyncRewriter.Server.csproj"

# Copy source code
COPY . .

# Build
WORKDIR "/src/src/AsyncRewriter.Server"
RUN dotnet build "AsyncRewriter.Server.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "AsyncRewriter.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AsyncRewriter.Server.dll"]
