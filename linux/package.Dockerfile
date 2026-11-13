# The Linux packaging image for scripts/linux-package.ps1 (#197): Ubuntu 24.04 (noble) with the .NET 10 SDK, plus what
# linux/package.sh needs for the sandbox extension (gcc, pkg-config, the WebKitGTK 6 development files).
# Multi-arch: build it with --platform linux/amd64 or linux/arm64 (arm64 runs under QEMU on an x64 PC).
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble
RUN apt-get update \
 && apt-get install -y --no-install-recommends gcc libc6-dev pkg-config libwebkitgtk-6.0-dev \
 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
