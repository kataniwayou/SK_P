# syntax=docker/dockerfile:1.7
# Phase 19 D-09 (INFRA-RMQ-02) — first runnable Orchestrator container.
# Mirrors the root Dockerfile multi-stage net8.0 structure, swapping the COPY restore-cache
# list + publish target to Orchestrator's reference closure (Messaging.Contracts + BaseConsole.Core
# + Orchestrator — NO BaseApi.* per D-08). Runtime base is aspnet:8.0 (NOT runtime:8.0) because
# BaseConsole.Core carries FrameworkReference Microsoft.AspNetCore.App for the embedded Kestrel
# health listener, which needs the ASP.NET Core shared framework at runtime. Port 8081 matches
# ConsoleHealth:Port (Phase 18 D-04).
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
# Cache layer: restore depends only on csproj + props + lock files
COPY ["Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY ["src/Messaging.Contracts/Messaging.Contracts.csproj", "src/Messaging.Contracts/"]
COPY ["src/BaseConsole.Core/BaseConsole.Core.csproj", "src/BaseConsole.Core/"]
COPY ["src/Orchestrator/Orchestrator.csproj", "src/Orchestrator/"]
RUN dotnet restore "src/Orchestrator/Orchestrator.csproj"
# Build layer: full source
COPY src/ src/
RUN dotnet publish "src/Orchestrator/Orchestrator.csproj" -c Release -o /publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
COPY --from=build /publish .
# Phase 19 fix(19-04): the aspnet:8.0-bookworm-slim runtime ships NEITHER wget NOR curl, so the
# compose `["CMD", "wget", "--spider", ...]` /health/ready healthcheck cannot exec ("wget: not found")
# and Docker marks the container unhealthy even though the app + bus start fine. Install wget (root,
# so BEFORE `USER app`) in a single clean layer to make the established wget --spider idiom work.
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \
    && rm -rf /var/lib/apt/lists/*
USER app
ENV ASPNETCORE_URLS=http://+:8081
EXPOSE 8081
ENTRYPOINT ["dotnet", "Orchestrator.dll"]
