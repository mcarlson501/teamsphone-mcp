# syntax=docker/dockerfile:1.7

ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0.422-bookworm-slim@sha256:63ebabdcde24cc8134304a6f50719cf1e22bb4e9f7148ac4ef967c7680187356
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:8.0.28-bookworm-slim@sha256:e78fda31142e28746a6908e288e0d40346793f691ca99d8d150bcbe95c0ef035

FROM --platform=$BUILDPLATFORM ${DOTNET_SDK_IMAGE} AS build
ARG TARGETARCH
ARG POWERSHELL_VERSION=7.4.6
ARG POWERSHELL_AMD64_SHA256=6f6015203c47806c5cc444c19d8ed019695e610fbd948154264bf9ca8e157561
ARG POWERSHELL_ARM64_SHA256=c0159b03e85f44ae1e7697818a011558da6c813d0aae848bf5ac13bf435d8624
ARG MICROSOFT_TEAMS_VERSION=7.8.0
ARG MICROSOFT_TEAMS_SHA256=d9aba188ba2bbf33f3fd08d18cee4e6ee7f28e4862c055fed42f45a9568cc160
WORKDIR /src
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl unzip \
    && case "${TARGETARCH}" in \
        amd64) POWERSHELL_ARCH=x64; POWERSHELL_SHA256="${POWERSHELL_AMD64_SHA256}" ;; \
        arm64) POWERSHELL_ARCH=arm64; POWERSHELL_SHA256="${POWERSHELL_ARM64_SHA256}" ;; \
        *) echo "Unsupported target architecture: ${TARGETARCH}" >&2; exit 1 ;; \
       esac \
    && curl --fail --location --silent --show-error \
        "https://github.com/PowerShell/PowerShell/releases/download/v${POWERSHELL_VERSION}/powershell-${POWERSHELL_VERSION}-linux-${POWERSHELL_ARCH}.tar.gz" \
        --output /tmp/powershell.tar.gz \
    && echo "${POWERSHELL_SHA256}  /tmp/powershell.tar.gz" | sha256sum --check --strict \
    && mkdir -p /powershell \
    && tar -xzf /tmp/powershell.tar.gz -C /powershell \
    && chmod 0755 /powershell/pwsh \
    && curl --fail --location --silent --show-error \
        "https://www.powershellgallery.com/api/v2/package/MicrosoftTeams/${MICROSOFT_TEAMS_VERSION}" \
        --output /tmp/MicrosoftTeams.nupkg \
    && echo "${MICROSOFT_TEAMS_SHA256}  /tmp/MicrosoftTeams.nupkg" | sha256sum --check --strict \
    && mkdir -p "/powershell/Modules/MicrosoftTeams/${MICROSOFT_TEAMS_VERSION}" \
    && unzip -q /tmp/MicrosoftTeams.nupkg \
        -d "/powershell/Modules/MicrosoftTeams/${MICROSOFT_TEAMS_VERSION}" \
    && rm -rf /var/lib/apt/lists/* /tmp/MicrosoftTeams.nupkg /tmp/powershell.tar.gz \
    && test -x /powershell/pwsh \
    && test -f "/powershell/Modules/MicrosoftTeams/${MICROSOFT_TEAMS_VERSION}/MicrosoftTeams.psd1"

COPY Directory.Build.props global.json ./
COPY src/TeamsPhoneMcp.Audit/TeamsPhoneMcp.Audit.csproj src/TeamsPhoneMcp.Audit/packages.lock.json src/TeamsPhoneMcp.Audit/
COPY src/TeamsPhoneMcp.Core/TeamsPhoneMcp.Core.csproj src/TeamsPhoneMcp.Core/packages.lock.json src/TeamsPhoneMcp.Core/
COPY src/TeamsPhoneMcp.Credentials/TeamsPhoneMcp.Credentials.csproj src/TeamsPhoneMcp.Credentials/packages.lock.json src/TeamsPhoneMcp.Credentials/
COPY src/TeamsPhoneMcp.Host/TeamsPhoneMcp.Host.csproj src/TeamsPhoneMcp.Host/packages.lock.json src/TeamsPhoneMcp.Host/
RUN dotnet restore src/TeamsPhoneMcp.Host/TeamsPhoneMcp.Host.csproj --locked-mode

COPY src/ src/
COPY tools/ tools/
RUN dotnet publish src/TeamsPhoneMcp.Host/TeamsPhoneMcp.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM --platform=$TARGETPLATFORM ${DOTNET_RUNTIME_IMAGE} AS final
ARG MICROSOFT_TEAMS_VERSION=7.8.0

WORKDIR /app
COPY --from=build /powershell /opt/microsoft/powershell/7
RUN ln -s /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh
COPY --from=build /app/publish/ .

RUN mkdir -p /data/audit && chown -R app:app /app /data/audit

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    PowerShellTenantSession__ModuleName=MicrosoftTeams \
    PowerShellTenantSession__ModuleRequiredVersion=${MICROSOFT_TEAMS_VERSION}

USER app
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD pwsh -NoLogo -NoProfile -Command \
        "\$response = Invoke-WebRequest -Uri http://127.0.0.1:8080/mcp -SkipHttpErrorCheck; if (\$response.StatusCode -eq 401) { exit 0 }; exit 1"

ENTRYPOINT ["dotnet", "TeamsPhoneMcp.Host.dll"]