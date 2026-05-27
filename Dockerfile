# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
# Cache layer: restore depends only on csproj + props + lock files
COPY ["Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY ["src/BaseApi.Core/BaseApi.Core.csproj", "src/BaseApi.Core/"]
COPY ["src/BaseApi.Service/BaseApi.Service.csproj", "src/BaseApi.Service/"]
RUN dotnet restore "src/BaseApi.Service/BaseApi.Service.csproj"
# Build layer: full source
COPY src/ src/
RUN dotnet publish "src/BaseApi.Service/BaseApi.Service.csproj" -c Release -o /publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
COPY --from=build /publish .
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BaseApi.Service.dll"]
