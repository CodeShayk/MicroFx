# Build from the repository root:
#   docker build -f src/MicroFx.Host.Service/Dockerfile -t microfx-host-service .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the manifests alone, so a source-only change reuses the cached restore layer.
#
# EVERY project in the reference graph must be copied, including the analyzer. Omitting one does
# not fail the restore — NuGet logs "Skipping project ... because it was not found" and carries on,
# leaving an incomplete assets file that only fails later, during publish, as a missing package.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/MicroFx/MicroFx.csproj src/MicroFx/packages.lock.json src/MicroFx/
COPY src/MicroFx.Analyzers/MicroFx.Analyzers.csproj src/MicroFx.Analyzers/packages.lock.json src/MicroFx.Analyzers/
COPY src/MicroFx.Host.Service/MicroFx.Host.Service.csproj src/MicroFx.Host.Service/packages.lock.json src/MicroFx.Host.Service/

# Locked mode, matching CI: the image is built from exactly the dependency graph that was reviewed.
RUN dotnet restore src/MicroFx.Host.Service/MicroFx.Host.Service.csproj --locked-mode

COPY src/ src/
RUN dotnet publish src/MicroFx.Host.Service/MicroFx.Host.Service.csproj \
        --configuration Release \
        --no-restore \
        --output /app

# Chiselled base: no shell, no package manager, non-root by default, minimal CVE surface.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .

# 8080 serves traffic; 8081 serves health and diagnostics and must never be published
# to a load balancer or exposed beyond the pod or task network.
EXPOSE 8080
EXPOSE 8081

# $APP_UID is the non-root user the chiselled images define. The application directory stays
# owned by root and read-only to this user, so the running process cannot rewrite its own code.
USER $APP_UID

ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["./MicroFx.Host.Service"]
