// Package transport carries agent->server traffic over HTTPS with cert pinning.
package transport

import (
	"bytes"
	"context"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"time"

	"github.com/walljm/jmwagent/internal/shared/proto"
)

// Client is the agent-side HTTP client.
type Client struct {
	BaseURL    string // e.g. https://server.lan:8443
	PSK        string
	PinnedSHA  string // hex-encoded SHA-256 of the server cert; "" disables pinning
	HTTPClient *http.Client
}

// New constructs a client that pins the server cert by SHA-256 fingerprint
// (lowercase hex). If pinnedSHA is empty, it accepts any cert (used for the
// very first registration call when the agent does not yet know the cert).
func New(baseURL, psk, pinnedSHA string) *Client {
	tr := &http.Transport{
		TLSClientConfig: &tls.Config{
			InsecureSkipVerify: true, // we verify by SHA-256 below
			VerifyPeerCertificate: func(rawCerts [][]byte, _ [][]*x509.Certificate) error {
				if pinnedSHA == "" {
					return nil // first contact: trust on first use
				}
				if len(rawCerts) == 0 {
					return errors.New("no peer cert")
				}
				sum := sha256.Sum256(rawCerts[0])
				got := hex.EncodeToString(sum[:])
				if got != pinnedSHA {
					return fmt.Errorf("server cert pin mismatch: got %s expected %s", got, pinnedSHA)
				}
				return nil
			},
		},
	}
	return &Client{
		BaseURL:   baseURL,
		PSK:       psk,
		PinnedSHA: pinnedSHA,
		HTTPClient: &http.Client{
			Transport: tr,
			Timeout:   30 * time.Second,
		},
	}
}

// Register sends a registration request.
func (c *Client) Register(ctx context.Context, req *proto.RegisterRequest) (*proto.RegisterResponse, error) {
	var resp proto.RegisterResponse
	if err := c.do(ctx, "/api/v1/agent/register", req, &resp); err != nil {
		return nil, err
	}
	return &resp, nil
}

// Heartbeat sends a heartbeat.
func (c *Client) Heartbeat(ctx context.Context, req *proto.HeartbeatRequest) (*proto.HeartbeatResponse, error) {
	var resp proto.HeartbeatResponse
	if err := c.do(ctx, "/api/v1/agent/heartbeat", req, &resp); err != nil {
		return nil, err
	}
	return &resp, nil
}

// Metrics submits a batch of snapshots.
func (c *Client) Metrics(ctx context.Context, req *proto.MetricsRequest) (*proto.MetricsResponse, error) {
	var resp proto.MetricsResponse
	if err := c.do(ctx, "/api/v1/agent/metrics", req, &resp); err != nil {
		return nil, err
	}
	return &resp, nil
}

// Discoveries submits a batch of network sightings.
func (c *Client) Discoveries(ctx context.Context, req *proto.DiscoveryRequest) (*proto.DiscoveryResponse, error) {
	var resp proto.DiscoveryResponse
	if err := c.do(ctx, "/api/v1/agent/discoveries", req, &resp); err != nil {
		return nil, err
	}
	return &resp, nil
}

// Inventory submits the device fact inventory.
func (c *Client) Inventory(ctx context.Context, req *proto.InventoryRequest) (*proto.InventoryResponse, error) {
	var resp proto.InventoryResponse
	if err := c.do(ctx, "/api/v1/agent/inventory", req, &resp); err != nil {
		return nil, err
	}
	return &resp, nil
}

// Download fetches a binary blob from the server. path must begin with "/".
// The caller is responsible for closing the returned reader. Uses a fresh
// context-bound request rather than the JSON helper because update payloads
// can be tens of MB and should not be subject to the 30s client timeout.
func (c *Client) Download(ctx context.Context, path string) (io.ReadCloser, int64, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.BaseURL+path, nil)
	if err != nil {
		return nil, 0, err
	}
	req.Header.Set("X-Agent-PSK", c.PSK)
	// Reuse the pinned-TLS transport but bypass the 30s timeout.
	httpClient := &http.Client{Transport: c.HTTPClient.Transport, Timeout: 10 * time.Minute}
	resp, err := httpClient.Do(req)
	if err != nil {
		return nil, 0, err
	}
	if resp.StatusCode >= 400 {
		b, _ := io.ReadAll(resp.Body)
		_ = resp.Body.Close()
		return nil, 0, fmt.Errorf("server %d: %s", resp.StatusCode, string(b))
	}
	return resp.Body, resp.ContentLength, nil
}

func (c *Client) do(ctx context.Context, path string, in, out any) error {
	body, err := json.Marshal(in)
	if err != nil {
		return err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.BaseURL+path, bytes.NewReader(body))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-Agent-PSK", c.PSK)
	resp, err := c.HTTPClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 400 {
		b, _ := io.ReadAll(resp.Body)
		return fmt.Errorf("server %d: %s", resp.StatusCode, string(b))
	}
	if out == nil {
		return nil
	}
	return json.NewDecoder(resp.Body).Decode(out)
}
