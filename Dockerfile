# Stripe CRM Bridge — Docker image
#
# Deployment target: Google Cloud Run (scales to zero, ~free at low volume)
#
# Build locally:
#   docker build -t stripe-crm-bridge .
#   docker run -p 8080:8080 \
#     -e Stripe__SecretKey=sk_test_xxx \
#     -e Stripe__WebhookSecret=whsec_xxx \
#     -e HubSpot__Enabled=true \
#     -e HubSpot__AccessToken=pat-na1-xxx \
#     stripe-crm-bridge

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/BeneficialStrategies.StripeCrmBridge/ BeneficialStrategies.StripeCrmBridge/

# No separate `dotnet restore` layer + `--no-restore` here on purpose: this workflow's plain
# `docker build .` (no --cache-from/--cache-to) gets no Docker layer cache across runs on
# GitHub Actions' ephemeral runners anyway, so splitting restore into its own layer buys
# nothing here. The split (with no packages.lock.json to make restore deterministic) is also
# the prime suspect for a real CI failure (2026-09-12): NETSDK1064 on a transitive dependency,
# Google.Api.CommonProtos 2.6.0, that was confirmed still present and downloadable on
# NuGet.org's CDN throughout — i.e. not actually missing, exactly the kind of restore/publish
# asset-file mismatch this pattern is known to produce. Couldn't force a local repro to
# confirm that's the *whole* story rather than a one-off NuGet CDN hiccup, but a single
# `dotnet publish` (which restores internally) verified clean in this same Linux image and
# is the more robust default either way.
RUN dotnet publish BeneficialStrategies.StripeCrmBridge/BeneficialStrategies.StripeCrmBridge.csproj \
    -c Release \
    -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

RUN apk add --no-cache curl

COPY --from=build /app/publish .

RUN mkdir -p /app/logs

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

EXPOSE 8080

RUN adduser -D -u 1000 appuser && chown -R appuser:appuser /app
USER appuser

ENTRYPOINT ["dotnet", "BeneficialStrategies.StripeCrmBridge.dll"]
