# syntax=docker/dockerfile:1.7
# Phase 58 (CFG-08) — the config-incompatible Processor.BadConfig container (D-01).
# A verbatim clone of the sample processor Dockerfile's multi-stage net8.0 structure, swapping the
# four sample references to this project. Runtime base is aspnet:8.0 (NOT runtime:8.0)
# because BaseConsole.Core carries FrameworkReference Microsoft.AspNetCore.App for the embedded Kestrel
# health listener, which needs the ASP.NET Core shared framework at runtime. Port 8082 matches
# ConsoleHealth:Port (appsettings.json).
#
# The SourceHash.targets compute/emit runs INSIDE this Linux `dotnet publish` — a separate project dir
# yields a distinct genuine embedded 64-hex (its own DB Processor row + procId), with no extra effort.
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
# Cache layer: restore depends only on csproj + props + lock files.
COPY ["Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY ["src/Messaging.Contracts/Messaging.Contracts.csproj", "src/Messaging.Contracts/"]
COPY ["src/BaseConsole.Core/BaseConsole.Core.csproj", "src/BaseConsole.Core/"]
COPY ["src/BaseProcessor.Core/BaseProcessor.Core.csproj", "src/BaseProcessor.Core/"]
COPY ["src/Processor.BadConfig/Processor.BadConfig.csproj", "src/Processor.BadConfig/"]
RUN dotnet restore "src/Processor.BadConfig/Processor.BadConfig.csproj"
# Build layer: full source — the SourceHash target folds the impl .cs here, embedding the 64-hex.
COPY src/ src/
RUN dotnet publish "src/Processor.BadConfig/Processor.BadConfig.csproj" -c Release -o /publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
# The aspnet:8.0-bookworm-slim runtime ships NEITHER wget NOR curl, so the compose
# `["CMD", "wget", "--spider", ...]` /health/ready healthcheck cannot exec. Install wget (root,
# so BEFORE `USER app`) in a single clean layer to make the established wget --spider idiom work.
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /publish .
USER app
ENV ASPNETCORE_URLS=http://+:8082
EXPOSE 8082
ENTRYPOINT ["dotnet", "Processor.BadConfig.dll"]
