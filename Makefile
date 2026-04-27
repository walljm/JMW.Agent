# JMW.Agent Makefile

BINARY_DIR := bin
VERSION := $(shell git describe --tags --always --dirty 2>/dev/null || echo "dev")
LDFLAGS := -ldflags "-s -w -X github.com/walljm/jmwagent/internal/shared/version.Version=$(VERSION)"

.PHONY: all build build-server build-agent test lint clean run-server run-agent docker-agent docker-agent-arm64

all: build

build: build-server build-agent

build-server:
	@mkdir -p $(BINARY_DIR)
	CGO_ENABLED=0 go build $(LDFLAGS) -o $(BINARY_DIR)/jmw-server ./cmd/server

build-agent:
	@mkdir -p $(BINARY_DIR)
	CGO_ENABLED=0 go build $(LDFLAGS) -o $(BINARY_DIR)/jmw-agent ./cmd/agent

build-all: build-linux-amd64 build-linux-arm64 build-darwin-amd64 build-darwin-arm64

build-linux-amd64:
	@mkdir -p $(BINARY_DIR)
	CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build $(LDFLAGS) -o $(BINARY_DIR)/jmw-server-linux-amd64 ./cmd/server
	CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build $(LDFLAGS) -o $(BINARY_DIR)/jmw-agent-linux-amd64 ./cmd/agent

build-linux-arm64:
	@mkdir -p $(BINARY_DIR)
	CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build $(LDFLAGS) -o $(BINARY_DIR)/jmw-server-linux-arm64 ./cmd/server
	CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build $(LDFLAGS) -o $(BINARY_DIR)/jmw-agent-linux-arm64 ./cmd/agent

build-darwin-amd64:
	@mkdir -p $(BINARY_DIR)
	CGO_ENABLED=0 GOOS=darwin GOARCH=amd64 go build $(LDFLAGS) -o $(BINARY_DIR)/jmw-server-darwin-amd64 ./cmd/server
	CGO_ENABLED=0 GOOS=darwin GOARCH=amd64 go build $(LDFLAGS) -o $(BINARY_DIR)/jmw-agent-darwin-amd64 ./cmd/agent

build-darwin-arm64:
	@mkdir -p $(BINARY_DIR)
	CGO_ENABLED=0 GOOS=darwin GOARCH=arm64 go build $(LDFLAGS) -o $(BINARY_DIR)/jmw-server-darwin-arm64 ./cmd/server
	CGO_ENABLED=0 GOOS=darwin GOARCH=arm64 go build $(LDFLAGS) -o $(BINARY_DIR)/jmw-agent-darwin-arm64 ./cmd/agent

test:
	go test ./...

vet:
	go vet ./...

lint: vet
	gofmt -l . | tee /dev/stderr | wc -l | xargs -I {} test {} -eq 0

fmt:
	gofmt -w .

clean:
	rm -rf $(BINARY_DIR)

run-server: build-server
	./$(BINARY_DIR)/jmw-server

run-agent: build-agent
	./$(BINARY_DIR)/jmw-agent

# Docker image build for the agent. Stamps VERSION into the binary so the
# server-side UI shows a real version instead of "dev".
DOCKER_IMAGE_AGENT ?= walljm/jmw-agent:latest

docker-agent:
	docker buildx build \
		--platform=linux/amd64 \
		-f deploy/docker/Dockerfile.agent \
		--build-arg VERSION=$(VERSION) \
		-t $(DOCKER_IMAGE_AGENT) \
		--push .

docker-agent-arm64:
	docker buildx build \
		--platform=linux/arm64 \
		-f deploy/docker/Dockerfile.agent \
		--build-arg VERSION=$(VERSION) \
		-t $(DOCKER_IMAGE_AGENT) \
		--push .
