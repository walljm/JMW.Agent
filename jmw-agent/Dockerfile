# Builds the prebuilt Home Assistant add-on image used by the published
# add-on repository.

FROM --platform=$BUILDPLATFORM golang:1.26.4-alpine@sha256:f23e8b227fb4493eabe03bede4d5a32d04092da71962f1fb79b5f7d1e6c2a17f AS build
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
