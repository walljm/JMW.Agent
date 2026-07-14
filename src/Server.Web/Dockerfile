FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src

# Copy root-level build props, analyzer config, and solution first (cache layer for restore).
# .editorconfig carries the repo's analyzer severity overrides (e.g. CA1720/CA1305) — without it,
# the compiler falls back to each analyzer's hard-coded default severity instead of this repo's
# actual policy, which can turn an intentionally-relaxed rule into a build-breaking error.
COPY .editorconfig .
COPY Directory.Build.props .
COPY JMW.Discovery.slnx .

# Copy project files for restore
COPY lib/ITPIE.Database.Abstractions/ITPIE.Database.Abstractions.csproj lib/ITPIE.Database.Abstractions/
COPY lib/ITPIE.Database.Generators/ITPIE.Database.Generators.csproj lib/ITPIE.Database.Generators/
COPY lib/ITPIE.Migrations/ITPIE.Migrations.csproj lib/ITPIE.Migrations/
COPY src/Core/JMW.Discovery.Core.csproj src/Core/
COPY src/Server.Web/JMW.Discovery.Server.csproj src/Server.Web/

# Copy all source then restore+publish in one step (avoids global package cache issues)
COPY lib/ lib/
COPY src/Core/ src/Core/
COPY src/Server.Web/ src/Server.Web/

RUN dotnet publish src/Server.Web/JMW.Discovery.Server.csproj \
    -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Trust the WallJM Home root CA for the server's OWN outbound HTTPS calls (e.g. OIDC discovery/
# token exchange against a private-CA-issued host like core.home). This is separate from
# JMW_TRUSTED_CA_PATH, which only controls what gets distributed to agents at runtime — this CA
# goes into the container's own OS trust store at build time, since it's a committed repo file,
# not a runtime secret.
COPY deploy/trusted-ca/walljm-home-root-ca.crt /usr/local/share/ca-certificates/walljm-home-root-ca.crt

# Install wget for healthcheck + ca-certificates for update-ca-certificates, create non-root
# user for security hardening.
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget ca-certificates \
    && update-ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system jmw \
    && useradd --system --gid jmw --no-create-home jmw \
    && mkdir -p /var/lib/jmw-server/keys \
    && chown -R jmw:jmw /var/lib/jmw-server /app

USER jmw

ENV ASPNETCORE_URLS=http://+:8080
ENV JMW_DB_CONNECTION=""
ENV JMW_KEY_RING_PATH=/var/lib/jmw-server/keys

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD wget -qO- http://localhost:8080/readyz || exit 1

ENTRYPOINT ["dotnet", "JMW.Discovery.Server.dll"]
