# Control-plane API container. Build context is the REPOSITORY ROOT:
#   docker build -f services/control-plane-api/Dockerfile -t control-plane-api .
# Non-production/staging image. Production images are built and signed via the
# release pipeline with approved identities (see docs/operations).

# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against pinned SDK + lockfile first (better layer caching).
COPY global.json Directory.Build.props ./
COPY services/control-plane-api/ControlPlane.Api.csproj services/control-plane-api/packages.lock.json services/control-plane-api/
RUN dotnet restore services/control-plane-api/ControlPlane.Api.csproj --locked-mode

COPY services/control-plane-api/ services/control-plane-api/
RUN dotnet publish services/control-plane-api/ControlPlane.Api.csproj \
      -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Run as the image's built-in non-root user.
USER $APP_UID
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

# Container healthcheck hits the liveness endpoint.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD ["dotnet", "--info"]

ENTRYPOINT ["dotnet", "ControlPlane.Api.dll"]
