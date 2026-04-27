# Home Assistant ships only the add-on directory as Docker build context, so
# the agent binary is pre-built on the dev machine (`make build-linux-arm64`
# etc.) and dropped in next to this Dockerfile as `jmw-agent.${BUILD_ARCH}`.
ARG BUILD_FROM
FROM ${BUILD_FROM}

ARG BUILD_ARCH
# We deliberately do NOT set JMW_HOST_ROOT here. Reads of /proc and /sys
# already see host data thanks to host_pid + procfs being kernel-backed.
# For host-specific files (/etc/os-release, hostname, etc.) we query the
# Supervisor /host/info API instead — bind-mounting the host root isn't
# possible from an HA add-on, and /proc/1/root doesn't reliably traverse
# the host mount namespace from inside a privileged container.

COPY jmw-agent.${BUILD_ARCH} /usr/bin/jmw-agent
RUN chmod +x /usr/bin/jmw-agent

COPY run.sh /run.sh
RUN chmod +x /run.sh

CMD ["/run.sh"]
