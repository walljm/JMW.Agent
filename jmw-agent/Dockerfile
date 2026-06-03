# Builds the prebuilt Home Assistant add-on image used by the published
# add-on repository.

FROM --platform=$BUILDPLATFORM golang:1.26-alpine@sha256:91eda9776261207ea25fd06b5b7fed8d397dd2c0a283e77f2ab6e91bfa71079d AS build
WORKDIR /src

COPY go.mod go.sum ./
RUN go mod download

COPY cmd ./cmd
COPY internal ./internal

ARG TARGETARCH
ARG BINARY_VERSION=dev
RUN CGO_ENABLED=0 GOOS=linux GOARCH=${TARGETARCH} \
    go build -trimpath \
        -ldflags="-s -w -X github.com/walljm/jmwagent/internal/shared/version.Version=${BINARY_VERSION}" \
        -o /out/jmw-agent ./cmd/agent

FROM ghcr.io/home-assistant/base:latest

ARG ADDON_VERSION=dev
LABEL \
    io.hass.version="${ADDON_VERSION}" \
    io.hass.type="app" \
    io.hass.arch="aarch64|amd64"

COPY --from=build /out/jmw-agent /usr/bin/jmw-agent
COPY jmw-agent/run.sh /run.sh
RUN chmod +x /usr/bin/jmw-agent /run.sh

CMD ["/run.sh"]
