# ------------------------------------------------------------
# Build
# ------------------------------------------------------------

FROM --platform=${BUILDPLATFORM} mcr.microsoft.com/dotnet/sdk:10.0-alpine3.24@sha256:0294f285d062afacc8b5f6d7febb0cd4ee879320e5585fa15f2f4ebf1feb9ba7 AS build

ARG TARGETARCH
ARG TARGETVARIANT
ARG APP_VERSION

WORKDIR /src
COPY . .

RUN dotnet publish \
    source/ProjectMemory/ProjectMemory.csproj \
    -p:DebugType=embedded \
    -p:GenerateDocumentationFile=false \
    -p:Version="${APP_VERSION}" \
    -c Release \
    -a "${TARGETARCH}" \
    --verbosity n \
    --self-contained true \
    -o /app/publish




# ------------------------------------------------------------
# Runtime
# ------------------------------------------------------------

FROM alpine:3.24@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf AS final

RUN apk add --upgrade --no-cache \
        ca-certificates-bundle \
        curl \
        libgcc \
        libssl3 \
        libstdc++ \
        ripgrep \
        zlib \
    && /usr/bin/rg --pcre2-version \
    && mkdir -p /app /data \
    && chmod 0777 /data

WORKDIR /app

COPY --from=build /app/publish/ ./

ENV \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true \
    ProjectMemory__DataDirectory=/data

# ToDo: Add health check
#HEALTHCHECK --interval=30s --timeout=35s --start-period=30s --retries=3 \
#   CMD curl --fail --silent --show-error --max-time 30 http://127.0.0.1:8000/health/ready > /dev/null || exit 1

EXPOSE 8000
VOLUME ["/data"]
ENTRYPOINT ["./ProjectMemory"]
