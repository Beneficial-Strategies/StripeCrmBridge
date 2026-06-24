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

COPY src/BeneficialStrategies.StripeCrmBridge/BeneficialStrategies.StripeCrmBridge.csproj BeneficialStrategies.StripeCrmBridge/

RUN dotnet restore BeneficialStrategies.StripeCrmBridge/BeneficialStrategies.StripeCrmBridge.csproj

COPY src/BeneficialStrategies.StripeCrmBridge/ BeneficialStrategies.StripeCrmBridge/

RUN dotnet publish BeneficialStrategies.StripeCrmBridge/BeneficialStrategies.StripeCrmBridge.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

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
