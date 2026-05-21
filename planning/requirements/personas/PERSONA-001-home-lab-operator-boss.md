---
id: PERSONA-001
title: Home-Lab Operator (Boss)
status: draft
primary_goals: []
key_tasks: []
constraints: []
notes: 
---

## Profile

- **Name (in-context):** Boss (Jason Wall, GitHub: walljm)
- **Role:** Sole operator, owner, and end user of JMW.Agent.
- **Technical skill:** Highly technical — software engineer; comfortable with CLI, systemd, Docker, networking, SQL, and self-hosted infrastructure.
- **Environment:** Personal home network with multiple VLANs/subnets, mix of Linux servers (x86 and Raspberry Pi), macOS workstations, IoT devices, printers, Google Home / Chromecast devices, switches, APs, and phones.

## Goals

1. See at a glance which devices on the home network are alive and healthy.
2. Get notified when something breaks before family members notice.
3. Track historical trends (disk fill rate, traffic patterns, reboot frequency) to plan upgrades.
4. Inventory IoT/printer devices that can't run an agent, and get alerted when something new appears on the network.
5. Replace a stack of commercial tools (Netdata/Prometheus/etc.) with a single, owned, lightweight, hackable system.

## Pain Points (Existing Tools)

- Off-the-shelf tools are heavy, opinionated, and don't fit a single-household workflow.
- Multiple tools required to cover system metrics + network discovery + IoT visibility.
- SaaS dashboards leak data off the network or require accounts.
- Existing tools assume multi-tenant / multi-user models with auth complexity Boss doesn't need.

## How They Use the System

- **Daily:** Glances at dashboard from desktop or phone to confirm everything is green.
- **Reactive:** Receives a Discord/Pushover notification when a device goes offline or a threshold trips; opens the dashboard to drill in.
- **Periodic:** Checks discovered-device list when adding a new IoT device to confirm it joined cleanly; renames/tags it.
- **Maintenance:** Approves new agents from the dashboard when standing up a new server. Periodically reviews trends and adjusts thresholds.

## Decisions They Make

- All product, architecture, and design decisions for JMW.Agent (single stakeholder).
