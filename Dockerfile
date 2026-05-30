# syntax=docker/dockerfile:1.7
# Multi-stage build for BaseApi.Service (INFRA-05).
# Stage 1 (build): SDK image restores + publishes a Release build.
# Stage 2 (runtime): aspnet image runs the published output as non-root.

FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

# Copy csproj files first for layer-cached restore (D-05 build-context discipline).
# BaseApi.Service depends on BaseApi.Core, which depends on Messaging.Contracts
# (Phase 17 shared-L2-root extract); copy all three manifests so restore resolves
# the full project graph without the rest of the source.
COPY ["Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY ["src/Messaging.Contracts/Messaging.Contracts.csproj", "src/Messaging.Contracts/"]
COPY ["src/BaseApi.Core/BaseApi.Core.csproj", "src/BaseApi.Core/"]
COPY ["src/BaseApi.Service/BaseApi.Service.csproj", "src/BaseApi.Service/"]
RUN dotnet restore "src/BaseApi.Service/BaseApi.Service.csproj"

# Copy the rest of the source and publish.
COPY src/ src/
RUN dotnet publish "src/BaseApi.Service/BaseApi.Service.csproj" -c Release -o /publish --no-restore /p:UseAppHost=false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
# Phase 20 D-12 — install wget BEFORE USER app so the compose healthcheck
# (`wget --spider http://localhost:8080/health/ready`) can execute; the
# aspnet:8.0-bookworm-slim base ships neither wget nor curl.
RUN apt-get update \
 && apt-get install -y --no-install-recommends wget \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /publish .
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BaseApi.Service.dll"]
