# JMW Agent

Reports this Home Assistant host to a JMW server.

## Configuration

| Option | Type | Description |
|---|---|---|
| `server_url` | url | Base URL of the JMW server, e.g. `https://192.168.1.54:8443`. |
| `psk` | password | Pre-shared key from the server's `server.toml`. Auto-approves this agent on first connect. |
| `pinned_sha` | string | SHA-256 fingerprint of the server's TLS cert (lowercase hex, no colons). Pin this to defeat MITM. |
| `interval_secs` | int (5–3600) | Heartbeat interval. Default 30. |
| `inventory_interval_secs` | int (60–604800) | Full inventory push interval. Default 86400 (24h). |
| `include_packages` | bool | Include installed-package list in inventory. Off by default — large and noisy on HAOS. |

## Getting `pinned_sha`

From any machine that can reach the server:

```sh
openssl s_client -connect <server>:8443 -showcerts </dev/null 2>/dev/null \
  | openssl x509 -noout -fingerprint -sha256 \
  | tr -d ':' | cut -d= -f2 | tr A-Z a-z
```

## Troubleshooting

- **"connection refused" in logs** — routing or firewall between this network
  and the server's network. Test with `curl -k <server_url>/healthz` from the
  *Advanced SSH & Web Terminal* add-on.
- **"tls: certificate fingerprint mismatch"** — `pinned_sha` is wrong, or the
  server cert was rotated. Re-run the openssl command above.
- **Agent stuck "pending" in server UI** — the PSK doesn't match the server's
  `psk` in `server.toml`. Without auto-approval, you have to approve it
  manually in the UI, which is also fine.
