# ── Build stage ────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file first so layer caching skips restore on source-only changes.
COPY ChubbyBunniesLevels.Api/ChubbyBunniesLevels.Api.csproj ChubbyBunniesLevels.Api/
RUN dotnet restore ChubbyBunniesLevels.Api/ChubbyBunniesLevels.Api.csproj

COPY . .

RUN dotnet publish ChubbyBunniesLevels.Api/ChubbyBunniesLevels.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ── Runtime stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Azure App Service on Linux injects PORT; fall back to 8080 for local runs.
ENV ASPNETCORE_URLS=http://+:${PORT:-8080}

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "ChubbyBunniesLevels.Api.dll"]
