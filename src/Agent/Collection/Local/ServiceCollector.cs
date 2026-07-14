using System.Runtime.Versioning;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects failed/stopped service facts.
/// Linux:   systemctl --failed (systemd units in failed state).
/// macOS:   launchctl list — reports jobs with non-zero last exit status.
/// Windows: PowerShell Get-CimInstance Win32_Service — auto-start services not running.
/// </summary>
public sealed class ServiceCollector : OsDispatchLocalCollector
{
    public override string Name => "service";

    // ── Linux / systemd ───────────────────────────────────────────────────────

    protected override async Task CollectLinuxAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        string output = await CollectorHelper.RunAsync(
            "systemctl",
            "--failed --no-legend --plain --all",
            ct
        );

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                continue;
            }

            string unit = parts[0];
            facts.Add(Fact.Create(FactPaths.ServiceName, [deviceId, unit], unit));
            facts.Add(Fact.Create(FactPaths.ServiceActiveState, [deviceId, unit], parts[2]));
            facts.Add(Fact.Create(FactPaths.ServiceSubState, [deviceId, unit], parts[3]));
        }
    }

    // ── macOS / launchctl ─────────────────────────────────────────────────────

    protected override async Task CollectMacOsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // launchctl list columns: PID  Status  Label
        // Status "-" = running/never-run; non-zero = last exit code (failed).
        string output = await CollectorHelper.RunAsync("launchctl", "list", ct);

        bool first = true;
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (first)
            {
                first = false;
                continue;
            } // skip header

            string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            string status = parts[1];
            if (status is "-" or "0")
            {
                continue; // running or clean exit
            }

            if (!int.TryParse(status, out int exitCode))
            {
                continue;
            }

            string label = parts[2];
            facts.Add(Fact.Create(FactPaths.ServiceName, [deviceId, label], label));
            facts.Add(Fact.Create(FactPaths.ServiceActiveState, [deviceId, label], "failed"));
            facts.Add(Fact.Create(FactPaths.ServiceExitCode, [deviceId, label], exitCode));
        }
    }

    // ── Windows ───────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    protected override async Task CollectWindowsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // Report auto-start services that are not running.
        const string script = """
            Get-CimInstance Win32_Service |
            Where-Object { $_.StartMode -eq 'Auto' -and $_.State -ne 'Running' } |
            Select-Object Name, DisplayName, State, ExitCode |
            ConvertTo-Json -Compress
            """;

        List<ServiceRow> rows = await CollectorHelper.RunPsJsonAsync<ServiceRow>(script, ct);
        foreach (ServiceRow r in rows)
        {
            if (r.Name is null)
            {
                continue;
            }

            facts.Add(Fact.Create(FactPaths.ServiceName, [deviceId, r.Name], r.Name));
            facts.Add(Fact.Create(FactPaths.ServiceDisplayName, [deviceId, r.Name], r.DisplayName ?? ""));
            facts.Add(
                Fact.Create(FactPaths.ServiceActiveState, [deviceId, r.Name], r.State?.ToLowerInvariant() ?? "unknown")
            );
            if (r.ExitCode != 0)
            {
                facts.Add(Fact.Create(FactPaths.ServiceExitCode, [deviceId, r.Name], r.ExitCode));
            }
        }
    }

    private sealed class ServiceRow
    {
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? State { get; set; }
        public int ExitCode { get; set; }
    }
}