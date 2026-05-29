// Command jmw-server runs the dashboard + control plane.
package main

import (
	"context"
	"crypto/rand"
	"crypto/tls"
	"flag"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	"github.com/walljm/jmwagent/internal/server/config"
	"github.com/walljm/jmwagent/internal/server/dek"
	httpsrv "github.com/walljm/jmwagent/internal/server/http"
	"github.com/walljm/jmwagent/internal/server/store"
	tlsbootstrap "github.com/walljm/jmwagent/internal/server/tls"
	"github.com/walljm/jmwagent/internal/shared/version"
)

func main() {
	var (
		cfgPath   = flag.String("config", "server.toml", "path to server config")
		showVer   = flag.Bool("version", false, "print version and exit")
		insecure  = flag.Bool("insecure", false, "serve plain HTTP instead of HTTPS (dev only)")
		rotateKey = flag.Bool("rotate-key", false, "rotate the data encryption key and re-encrypt all secrets")
	)
	flag.Parse()

	if *showVer {
		fmt.Println(version.Version)
		return
	}

	logger := slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: slog.LevelInfo}))
	slog.SetDefault(logger)

	cfg, err := config.Load(*cfgPath)
	if err != nil {
		slog.Error("load config", "err", err)
		os.Exit(1)
	}

	if err := os.MkdirAll(cfg.DataDir, 0o755); err != nil {
		slog.Error("data dir", "err", err)
		os.Exit(1)
	}

	// Default cert paths inside data dir.
	if cfg.TLSCertFile == "" {
		cfg.TLSCertFile = filepath.Join(cfg.DataDir, "server.crt")
	}
	if cfg.TLSKeyFile == "" {
		cfg.TLSKeyFile = filepath.Join(cfg.DataDir, "server.key")
	}

	hostname, _ := os.Hostname()
	if hostname == "" {
		hostname = "jmw-server"
	}

	// First-boot bootstrap: cert + PSK.
	dirty := false
	sha, err := tlsbootstrap.EnsureCert(cfg.TLSCertFile, cfg.TLSKeyFile, hostname)
	if err != nil {
		slog.Error("tls bootstrap", "err", err)
		os.Exit(1)
	}
	if pskGen, err := config.EnsureAgentPSK(cfg); err != nil {
		slog.Error("ensure psk", "err", err)
		os.Exit(1)
	} else if pskGen {
		dirty = true
		slog.Info("generated agent PSK", "psk", cfg.AgentPSK)
		fmt.Fprintf(os.Stderr, "\n*** AGENT PSK (record this once): %s ***\n\n", cfg.AgentPSK)
	}
	if dirty {
		if err := config.Save(*cfgPath, cfg); err != nil {
			slog.Error("save config", "err", err)
			os.Exit(1)
		}
	}

	ctx, cancel := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer cancel()

	st, err := store.Open(ctx, cfg.DBPath())
	if err != nil {
		slog.Error("open store", "err", err)
		os.Exit(1)
	}
	defer st.Close()

	// Initialize data encryption key (for secrets at rest).
	dekPath := filepath.Join(cfg.DataDir, "dek.key")
	dataKey, err := dek.LoadOrCreate(dekPath)
	if err != nil {
		slog.Error("dek init", "err", err)
		os.Exit(1)
	}

	// DEK rotation subcommand.
	if *rotateKey {
		if err := runRotateKey(ctx, st, dataKey, dekPath); err != nil {
			slog.Error("rotate-key", "err", err)
			os.Exit(1)
		}
		return
	}

	srv, err := httpsrv.New(ctx, cfg, st, sha)
	if err != nil {
		slog.Error("http server", "err", err)
		os.Exit(1)
	}
	srv.StartBackground(ctx)

	httpSrv := &http.Server{
		Addr:              cfg.Listen,
		Handler:           srv.Router(),
		ReadHeaderTimeout: 10 * time.Second,
		IdleTimeout:       2 * time.Minute,
		TLSConfig:         &tls.Config{MinVersion: tls.VersionTLS12},
	}

	go func() {
		if *insecure {
			slog.Warn("serving HTTP (no TLS)", "addr", cfg.Listen)
			if err := httpSrv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
				slog.Error("http", "err", err)
				cancel()
			}
		} else {
			slog.Info("serving HTTPS", "addr", cfg.Listen, "cert_sha256", sha)
			if err := httpSrv.ListenAndServeTLS(cfg.TLSCertFile, cfg.TLSKeyFile); err != nil && err != http.ErrServerClosed {
				slog.Error("https", "err", err)
				cancel()
			}
		}
	}()

	<-ctx.Done()
	slog.Info("shutting down")
	shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer shutdownCancel()
	_ = httpSrv.Shutdown(shutdownCtx)
}

// runRotateKey generates a new DEK, re-encrypts all source secrets, and writes
// the new key file. The old key file is backed up as dek.key.bak.
func runRotateKey(ctx context.Context, st *store.Store, oldKey *dek.Key, dekPath string) error {
	slog.Info("rotating data encryption key")

	// Backup old key.
	backupPath := dekPath + ".bak"
	oldRaw, err := os.ReadFile(dekPath)
	if err != nil {
		return fmt.Errorf("read old key for backup: %w", err)
	}
	if err := os.WriteFile(backupPath, oldRaw, 0o600); err != nil {
		return fmt.Errorf("backup old key: %w", err)
	}

	// Generate new key.
	newKeyRaw := make([]byte, 32)
	if _, err := io.ReadFull(rand.Reader, newKeyRaw); err != nil {
		return fmt.Errorf("generate new key: %w", err)
	}
	if err := os.WriteFile(dekPath, newKeyRaw, 0o600); err != nil {
		// Restore backup.
		_ = os.WriteFile(dekPath, oldRaw, 0o600)
		return fmt.Errorf("write new key: %w", err)
	}

	// Load the new key.
	newKey, err := dek.LoadOrCreate(dekPath)
	if err != nil {
		_ = os.WriteFile(dekPath, oldRaw, 0o600)
		return fmt.Errorf("load new key: %w", err)
	}

	// Re-encrypt: decrypt with old key, encrypt with new key.
	count, err := st.RotateSecrets(ctx, oldKey, newKey)
	if err != nil {
		// Restore old key on failure.
		_ = os.WriteFile(dekPath, oldRaw, 0o600)
		return fmt.Errorf("re-encrypt with new key: %w", err)
	}

	slog.Info("DEK rotation complete", "sources_re_encrypted", count, "backup", backupPath)
	fmt.Fprintf(os.Stderr, "DEK rotated. Old key backed up to %s\n", backupPath)
	return nil
}
