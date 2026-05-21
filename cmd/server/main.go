// Command jmw-server runs the dashboard + control plane.
package main

import (
	"context"
	"crypto/tls"
	"flag"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	"github.com/walljm/jmwagent/internal/server/config"
	httpsrv "github.com/walljm/jmwagent/internal/server/http"
	"github.com/walljm/jmwagent/internal/server/store"
	tlsbootstrap "github.com/walljm/jmwagent/internal/server/tls"
	"github.com/walljm/jmwagent/internal/shared/version"
)

func main() {
	var (
		cfgPath  = flag.String("config", "server.toml", "path to server config")
		showVer  = flag.Bool("version", false, "print version and exit")
		insecure = flag.Bool("insecure", false, "serve plain HTTP instead of HTTPS (dev only)")
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

	srv, err := httpsrv.New(cfg, st, sha)
	if err != nil {
		slog.Error("http server", "err", err)
		os.Exit(1)
	}
	srv.StartBackground(ctx)

	httpSrv := &http.Server{
		Addr:              cfg.Addr,
		Handler:           srv.Router(),
		ReadHeaderTimeout: 10 * time.Second,
		IdleTimeout:       2 * time.Minute,
		TLSConfig:         &tls.Config{MinVersion: tls.VersionTLS12},
	}

	go func() {
		if *insecure {
			slog.Warn("serving HTTP (no TLS)", "addr", cfg.Addr)
			if err := httpSrv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
				slog.Error("http", "err", err)
				cancel()
			}
		} else {
			slog.Info("serving HTTPS", "addr", cfg.Addr, "cert_sha256", sha)
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
