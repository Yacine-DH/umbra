# The Linux packaging images for scripts/linux-package.ps1 (#197): Ubuntu 24.04 (noble) with the .NET 10 SDK, plus what
# linux/package.sh needs for the sandbox extension (a C compiler, pkg-config, the WebKitGTK 6 development files).
# Both targets run as linux/amd64: .NET publishes arm64 with -r linux-arm64, and only the extension is cross-compiled.
#   --target x64     native gcc + libwebkitgtk-6.0-dev
#   --target arm64   aarch64-linux-gnu-gcc + libwebkitgtk-6.0-dev:arm64 (multiarch, from ports.ubuntu.com)
#   --target native  the x64 recipe on whatever --platform is given (the -Qemu fallback: linux/arm64 emulated, slow)
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS base
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

FROM base AS x64
RUN apt-get update \
 && apt-get install -y --no-install-recommends gcc libc6-dev pkg-config libwebkitgtk-6.0-dev \
 && rm -rf /var/lib/apt/lists/*

FROM x64 AS native

FROM base AS arm64
# The archive mirrors carry amd64 only; arm64 packages come from ports.ubuntu.com.
RUN set -eu; src=/etc/apt/sources.list.d/ubuntu.sources; \
    sed -i '/^Types:/a Architectures: amd64' "$src"; \
    printf 'Types: deb\nURIs: http://ports.ubuntu.com/ubuntu-ports/\nSuites: noble noble-updates noble-security\nComponents: main universe\nArchitectures: arm64\nSigned-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\n' \
      > /etc/apt/sources.list.d/ubuntu-ports.sources; \
    dpkg --add-architecture arm64; \
    apt-get update; \
    apt-get install -y --no-install-recommends gcc-aarch64-linux-gnu libc6-dev-arm64-cross pkg-config libwebkitgtk-6.0-dev:arm64; \
    rm -rf /var/lib/apt/lists/*
ENV WEBEXT_CC=aarch64-linux-gnu-gcc \
    PKG_CONFIG_LIBDIR=/usr/lib/aarch64-linux-gnu/pkgconfig:/usr/share/pkgconfig
