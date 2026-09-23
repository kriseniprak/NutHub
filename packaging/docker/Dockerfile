# syntax=docker/dockerfile:1
#
# NutHub container image. Build from the repository root:
#   docker build -f packaging/docker/Dockerfile -t nuthub .
# Multi-architecture (the SDK stage runs natively and cross-publishes):
#   docker buildx build -f packaging/docker/Dockerfile --platform linux/amd64,linux/arm64,linux/arm/v7 -t nuthub .
#
# See docker-compose.yml for volumes, ports and USB / serial pass-through.

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src

# Restore first, from the project files only, so source edits do not invalidate the package layer.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/NutHub/NutHub.csproj src/NutHub/
COPY src/NutHub.Core/NutHub.Core.csproj src/NutHub.Core/
COPY src/NutHub.Protocol/NutHub.Protocol.csproj src/NutHub.Protocol/
COPY src/NutHub.Drivers.Hid/NutHub.Drivers.Hid.csproj src/NutHub.Drivers.Hid/
COPY src/NutHub.Drivers.Serial/NutHub.Drivers.Serial.csproj src/NutHub.Drivers.Serial/
COPY src/NutHub.Drivers.Snmp/NutHub.Drivers.Snmp.csproj src/NutHub.Drivers.Snmp/
COPY src/NutHub.Drivers.Net/NutHub.Drivers.Net.csproj src/NutHub.Drivers.Net/
COPY src/NutHub.Storage/NutHub.Storage.csproj src/NutHub.Storage/
COPY src/NutHub.Services/NutHub.Services.csproj src/NutHub.Services/
COPY src/NutHub.Web/NutHub.Web.csproj src/NutHub.Web/
RUN case "$TARGETARCH" in \
        amd64) echo linux-x64 ;; \
        arm64) echo linux-arm64 ;; \
        arm) echo linux-arm ;; \
        *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac > /tmp/rid \
    && dotnet restore src/NutHub/NutHub.csproj -r "$(cat /tmp/rid)"

COPY src/ src/
COPY packaging/linux/ packaging/linux/
# Self-contained (runs on runtime-deps, no .NET runtime in the image) but as a plain folder: a single-file
# executable would unpack its native libraries at every start.
RUN dotnet publish src/NutHub/NutHub.csproj -c Release -r "$(cat /tmp/rid)" --no-restore \
        -p:PublishSingleFile=false -p:DebugType=embedded -o /app \
    && rm -f /app/*.pdb

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION} AS runtime
LABEL org.opencontainers.image.title="NutHub" \
      org.opencontainers.image.description="UPS server compatible with Network UPS Tools, with a web panel" \
      org.opencontainers.image.source="https://github.com/kriseniprak/NutHub" \
      org.opencontainers.image.licenses="GPL-3.0-or-later"

ENV NUTHUB_DATA_DIR=/var/lib/nuthub \
    NUTHUB_CONFIG=/etc/nuthub/nuthub.json

# USB HID UPSes: HidSharp enumerates hidraw devices through libudev, which minimal images may lack.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libudev1 \
    && rm -rf /var/lib/apt/lists/*

# The images' non-root "app" account (APP_UID) owns the data and configuration.
COPY --from=build /app /app
RUN mkdir -p /var/lib/nuthub /etc/nuthub \
    && chown "$APP_UID:$APP_UID" /var/lib/nuthub /etc/nuthub \
    && chmod 0750 /var/lib/nuthub /etc/nuthub
USER $APP_UID

VOLUME ["/var/lib/nuthub", "/etc/nuthub"]
# 3493: NUT protocol; 8493: web panel (HTTP); 8494: web panel (HTTPS, when enabled).
EXPOSE 3493 8493 8494

# The runtime-deps image has no shell tools such as curl: NutHub checks itself (GET /api/auth/state on the local
# web panel, or VER on the NUT port when the panel is disabled).
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 CMD ["/app/nuthub", "healthcheck"]

# NutHub stops cleanly on SIGTERM (drivers close their devices); give it the time with "docker stop -t 30".
STOPSIGNAL SIGTERM
ENTRYPOINT ["/app/nuthub"]
CMD ["run"]
