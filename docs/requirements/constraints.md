---
agent: sdev-requirements
date: 2026-06-04
status: draft
---

# Constraints, Assumptions, and Out-of-Scope

## Constraints

### Technical Constraints

1. **Runtime**: C# .NET 10. The server and web application must be built in C# on .NET 10 to match the existing codebase.
2. **Database**: PostgreSQL. The schema already exists (`scratch/Schema.sql`); projection tables are the read model for all reporting views.
3. **No ORM**: Per project coding standards, queries are written in raw SQL using a typed query approach (sqlx or equivalent). No Entity Framework.
4. **Self-hosted deployment**: The application must run on a single host without cloud dependencies. Docker/container packaging is acceptable but not required.
5. **No external identity provider**: Authentication is handled locally (no OAuth2/OIDC integration with external IdPs in this iteration). Server manages its own user store.
6. **Session-based authentication**: Standard server-side sessions with secure, httpOnly, SameSite cookies. No JWT-based session management.
7. **Minimal dependencies**: New third-party dependencies must be justified. Prefer building in-house when the implementation effort is reasonable.
8. **Credentials must not be stored in plaintext**: SSH keys, passwords, SNMP strings, and API tokens stored in the credentials store must be encrypted at rest.
9. **Existing agent config format compatibility**: The web UI's collection configuration must be compatible with the existing `AgentConfig` model (`ServerUrl`, `Name`, `Zone`, `Interval`, `MaxConcurrency`, `Targets`, `Services`).
10. **URL-driven navigation**: All non-ephemeral navigation state (active tab, selected device, filter, pagination) must be reflected in the URL.

### Operational Constraints

11. **Data freshness**: The UI reflects projection table state. Projections are updated as facts arrive; the UI does not need real-time push — polling or manual refresh is acceptable for MVP.
12. **Device affinity**: The server architecture must route facts from the same device to the same server instance for EntityStateCache coherence. This is an existing server constraint, not a new UI requirement.

## Assumptions

1. The collection layer (agents, fact ingest pipeline, device/service registry, projection pipeline) is fully operational and not being changed in this iteration.
2. The PostgreSQL schema in `scratch/Schema.sql` is the authoritative read model. The web UI reads exclusively from projection tables for reporting.
3. Agent configuration is currently file-based. The web UI will provide an alternative configuration path; the existing file-based path remains valid and is not removed.
4. A single administrator deploys and operates the system. Multi-tenant or multi-organization isolation is not required.
5. The primary user is technically proficient (network engineer or systems administrator). No consumer-grade UX simplification is needed, but the UI should be clear and efficient.
6. The system will be used in English only. No internationalization or localization is required.
7. There is no existing web UI being replaced. This is a greenfield UI.
8. Agents communicate to the server over a trusted internal network. TLS for agent-to-server communication is desirable but not a hard requirement for this iteration; the UI itself should be served over HTTPS when deployed.
9. The expected fleet size is small-to-medium: up to a few hundred devices per deployment. Sub-second page loads are expected for up to 500 devices.
10. Backup and disaster recovery of the PostgreSQL database is the operator's responsibility; the application does not manage database backups.

## Out-of-Scope

The following topics were evaluated and explicitly excluded from this iteration:

- **Multi-tenancy / multi-organization**: Single-operator deployment only.
- **Mobile application**: Web UI is desktop/browser-first; responsive design for mobile is nice-to-have, not required.
- **Email or webhook alerting**: No notification system in this iteration.
- **Custom dashboard builder**: No user-configurable dashboards; the predefined reporting pages cover all use cases for now.
- **Public REST API**: No external API for third-party integrations.
- **Agent auto-update via web UI**: Agents are updated through OS package management or manual deployment.
- **Internationalization / localization**: English only.
- **OAuth2 / OIDC / SSO integration**: Local authentication only.
- **SLA / uptime guarantees**: No formal SLA. The system is a monitoring tool, not itself an HA production service.
- **Data export / import**: No bulk export or import of monitoring data.
- **Trend analysis / charting over time**: The change feed provides historical data; charts and trend graphs are deferred.
- **Scheduled report generation**: No automated report emails or exports.
- **Agent protocol collection discovery (SNMP MIBs, etc.)**: Collection protocols are configured manually via target entries.
- **Scalability beyond ~500 devices**: Requirements are scoped to small-to-medium deployments. Large enterprise scalability is a future concern.
