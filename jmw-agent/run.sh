#!/usr/bin/with-contenv bashio
# Translate the add-on's options.json (managed by Home Assistant Supervisor)
# into agent.toml, then exec the agent. /data is the persistent add-on volume,
# safe to use for both the generated config and the agent ID file.
set -euo pipefail

CONFIG=/data/agent.toml
ID_FILE=/data/agent.id

SERVER_URL=$(bashio::config 'server_url')
PSK=$(bashio::config 'psk')
PINNED_SHA=$(bashio::config 'pinned_sha')
INTERVAL=$(bashio::config 'interval_secs')
INV_INTERVAL=$(bashio::config 'inventory_interval_secs')
INCLUDE_PKGS=$(bashio::config 'include_packages')

if bashio::var.is_empty "${PSK}"; then
    bashio::exit.nok "psk is required - set it in the add-on configuration"
fi
if bashio::var.is_empty "${PINNED_SHA}"; then
    bashio::exit.nok "pinned_sha is required - get it via 'openssl s_client | openssl x509 -fingerprint -sha256'"
fi

cat > "${CONFIG}" <<EOF
server_url              = "${SERVER_URL}"
psk                     = "${PSK}"
pinned_sha              = "${PINNED_SHA}"
id_file                 = "${ID_FILE}"
interval_secs           = ${INTERVAL}
inventory_interval_secs = ${INV_INTERVAL}
include_packages        = ${INCLUDE_PKGS}
EOF

bashio::log.info "Starting jmw-agent -> ${SERVER_URL}"
exec /usr/bin/jmw-agent --config "${CONFIG}"
