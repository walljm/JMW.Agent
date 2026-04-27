// Package notify delivers alert notifications via configured channels.
package notify

import (
	"bytes"
	"context"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"net/http"
	"net/smtp"
	"strconv"
	"time"

	"github.com/walljm/jmwagent/internal/server/store"
)

// Send dispatches a notification through the channel.
func Send(ctx context.Context, ch *store.NotificationChannel, severity, message string) error {
	switch ch.Kind {
	case "webhook":
		return sendWebhook(ctx, ch.Config, severity, message)
	case "email":
		return sendEmail(ctx, ch.Config, severity, message)
	default:
		return fmt.Errorf("unknown channel kind: %s", ch.Kind)
	}
}

func sendWebhook(ctx context.Context, cfg map[string]any, severity, message string) error {
	url, _ := cfg["url"].(string)
	if url == "" {
		return fmt.Errorf("webhook channel missing url")
	}
	body, _ := json.Marshal(map[string]any{
		"severity":  severity,
		"message":   message,
		"timestamp": time.Now().UTC().Format(time.RFC3339),
		"source":    "jmw-server",
	})
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, url, bytes.NewReader(body))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	cli := &http.Client{Timeout: 10 * time.Second}
	resp, err := cli.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 400 {
		return fmt.Errorf("webhook %d", resp.StatusCode)
	}
	return nil
}

func sendEmail(ctx context.Context, cfg map[string]any, severity, message string) error {
	host, _ := cfg["host"].(string)
	portStr := fmt.Sprintf("%v", cfg["port"])
	port, _ := strconv.Atoi(portStr)
	if port == 0 {
		port = 587
	}
	user, _ := cfg["username"].(string)
	pass, _ := cfg["password"].(string)
	from, _ := cfg["from"].(string)
	to, _ := cfg["to"].(string)
	useTLS, _ := cfg["tls"].(bool)
	if host == "" || from == "" || to == "" {
		return fmt.Errorf("email channel missing host/from/to")
	}
	addr := fmt.Sprintf("%s:%d", host, port)
	subj := fmt.Sprintf("[JMW][%s] %s", severity, truncate(message, 70))
	body := fmt.Sprintf("From: %s\r\nTo: %s\r\nSubject: %s\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\n%s\r\n",
		from, to, subj, message)
	var auth smtp.Auth
	if user != "" {
		auth = smtp.PlainAuth("", user, pass, host)
	}
	if useTLS {
		c, err := smtp.Dial(addr)
		if err != nil {
			return err
		}
		defer c.Close()
		if err := c.StartTLS(&tls.Config{ServerName: host}); err != nil {
			return err
		}
		if auth != nil {
			if err := c.Auth(auth); err != nil {
				return err
			}
		}
		if err := c.Mail(from); err != nil {
			return err
		}
		if err := c.Rcpt(to); err != nil {
			return err
		}
		w, err := c.Data()
		if err != nil {
			return err
		}
		if _, err := w.Write([]byte(body)); err != nil {
			return err
		}
		if err := w.Close(); err != nil {
			return err
		}
		return c.Quit()
	}
	return smtp.SendMail(addr, auth, from, []string{to}, []byte(body))
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n-1] + "…"
}
