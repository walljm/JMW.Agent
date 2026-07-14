using System.Runtime.Versioning;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects pending OS update facts.
/// Linux:   apt / dnf / zypper.
/// macOS:   softwareupdate -l (slow: 30-60s — run on a longer cadence).
/// Windows: Windows Update COM API via PowerShell.
/// </summary>
public sealed class UpdatesCollector : OsDispatchLocalCollector
{
    public override string Name => "updates";

    // ── Linux ─────────────────────────────────────────────────────────────────

    protected override async Task CollectLinuxAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        facts.Add(
            Fact.Create(
                FactPaths.UpdateRebootRequired,
                [deviceId],
                File.Exists("/var/run/reboot-required")
            )
        );

        if (CollectorHelper.BinaryExists("apt"))
        {
            await CollectAptAsync(deviceId, facts, ct);
        }
        else if (CollectorHelper.BinaryExists("dnf"))
        {
            await CollectDnfAsync(deviceId, facts, ct);
        }
        else if (CollectorHelper.BinaryExists("zypper"))
        {
            await CollectZypperAsync(deviceId, facts, ct);
        }
    }

    private static async Task CollectAptAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        facts.Add(Fact.Create(FactPaths.UpdateManager, [deviceId], "apt"));
        string output = await CollectorHelper.RunAsync("apt", "list --upgradable", ct, timeoutSeconds: 60);
        int pending = 0, security = 0, reported = 0;

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed == "" || trimmed.StartsWith("Listing", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int slash = trimmed.IndexOf('/');
            if (slash < 0)
            {
                continue;
            }

            string pkg = trimmed[..slash];
            string[] rest = trimmed[(slash + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length < 2)
            {
                continue;
            }

            string repo = rest[0];
            string newVer = rest[1];
            bool isSec = repo.Contains("security", StringComparison.OrdinalIgnoreCase);
            pending++;
            if (isSec)
            {
                security++;
            }

            if (reported++ < 50)
            {
                facts.Add(Fact.Create(FactPaths.UpdatePendingName, [deviceId, pkg], pkg));
                facts.Add(Fact.Create(FactPaths.UpdatePendingNewVersion, [deviceId, pkg], newVer));
                facts.Add(Fact.Create(FactPaths.UpdatePendingSource, [deviceId, pkg], repo));
                facts.Add(Fact.Create(FactPaths.UpdatePendingSecurity, [deviceId, pkg], isSec));
            }
        }

        facts.Add(Fact.Create(FactPaths.UpdatePendingCount, [deviceId], pending));
        facts.Add(Fact.Create(FactPaths.UpdateSecurityCount, [deviceId], security));
    }

    private static async Task CollectDnfAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        facts.Add(Fact.Create(FactPaths.UpdateManager, [deviceId], "dnf"));
        string output = await CollectorHelper.RunAsync("dnf", "-q check-update", ct, timeoutSeconds: 60);
        int pending = 0, reported = 0;
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            pending++;
            if (reported++ < 50)
            {
                facts.Add(Fact.Create(FactPaths.UpdatePendingName, [deviceId, parts[0]], parts[0]));
                facts.Add(Fact.Create(FactPaths.UpdatePendingNewVersion, [deviceId, parts[0]], parts[1]));
                facts.Add(Fact.Create(FactPaths.UpdatePendingSource, [deviceId, parts[0]], parts[2]));
            }
        }

        facts.Add(Fact.Create(FactPaths.UpdatePendingCount, [deviceId], pending));
    }

    private static async Task CollectZypperAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        facts.Add(Fact.Create(FactPaths.UpdateManager, [deviceId], "zypper"));
        string output = await CollectorHelper.RunAsync("zypper", "-q list-updates", ct, timeoutSeconds: 60);
        int pending = output.Split('\n')
            .Count(l => l.TrimStart().StartsWith("v |", StringComparison.OrdinalIgnoreCase));
        facts.Add(Fact.Create(FactPaths.UpdatePendingCount, [deviceId], pending));
    }

    // ── macOS ─────────────────────────────────────────────────────────────────

    protected override async Task CollectMacOsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        facts.Add(Fact.Create(FactPaths.UpdateManager, [deviceId], "softwareupdate"));

        // softwareupdate -l can take 30-60s on first run.
        string output = await CollectorHelper.RunAsync("softwareupdate", "-l", ct, timeoutSeconds: 120);

        int pending = 0, reported = 0;
        bool rebootRequired = false;

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("* Label:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string label = trimmed["* Label:".Length..].Trim();
            pending++;

            // Updates with "restart" in the title require a reboot.
            if (output.Contains("restart", StringComparison.OrdinalIgnoreCase))
            {
                rebootRequired = true;
            }

            if (reported++ < 50)
            {
                facts.Add(Fact.Create(FactPaths.UpdatePendingName, [deviceId, label], label));
                facts.Add(Fact.Create(FactPaths.UpdatePendingSource, [deviceId, label], "softwareupdate"));
            }
        }

        facts.Add(Fact.Create(FactPaths.UpdatePendingCount, [deviceId], pending));
        facts.Add(Fact.Create(FactPaths.UpdateRebootRequired, [deviceId], rebootRequired));
    }

    // ── Windows ───────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    protected override async Task CollectWindowsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        facts.Add(Fact.Create(FactPaths.UpdateManager, [deviceId], "windowsupdate"));

        // Reboot-required: check three well-known registry keys.
        const string rebootScript = """
            $keys = @(
              'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending',
              'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired',
              'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations'
            )
            foreach ($k in $keys) { if (Test-Path $k) { 'true'; exit 0 } }
            'false'
            """;
        string rebootOut = await CollectorHelper.RunPsAsync(rebootScript, ct);
        facts.Add(Fact.Create(FactPaths.UpdateRebootRequired, [deviceId], rebootOut.Trim() == "true"));

        // Windows Update COM API — may take 30+ seconds on first run.
        const string wuScript = """
            try {
              $session  = New-Object -ComObject Microsoft.Update.Session
              $searcher = $session.CreateUpdateSearcher()
              $result   = $searcher.Search("IsInstalled=0 and IsHidden=0")
              $list = @()
              foreach ($u in $result.Updates) {
                $list += [pscustomobject]@{
                  Title    = $u.Title
                  KB       = ($u.KBArticleIDs -join ',')
                  Security = ($u.Categories | Where-Object { $_.Name -eq 'Security Updates' }).Count -gt 0
                }
              }
              $list | ConvertTo-Json -Compress -Depth 3
            } catch { '[]' }
            """;

        List<WuRow> rows = await CollectorHelper.RunPsJsonAsync<WuRow>(wuScript, ct);
        int security = 0, reported = 0;
        foreach (WuRow r in rows)
        {
            if (r.Security)
            {
                security++;
            }

            if (reported++ < 50)
            {
                string name = r.KB is { Length: > 0 } kb ? $"KB{kb}: {r.Title}" : r.Title ?? "";
                string pkg = name[..Math.Min(name.Length, 80)];
                facts.Add(Fact.Create(FactPaths.UpdatePendingName, [deviceId, pkg], name));
                facts.Add(Fact.Create(FactPaths.UpdatePendingSecurity, [deviceId, pkg], r.Security));
                facts.Add(Fact.Create(FactPaths.UpdatePendingSource, [deviceId, pkg], "WindowsUpdate"));
            }
        }

        facts.Add(Fact.Create(FactPaths.UpdatePendingCount, [deviceId], rows.Count));
        facts.Add(Fact.Create(FactPaths.UpdateSecurityCount, [deviceId], security));
    }

    private sealed class WuRow
    {
        public string? Title { get; set; }
        public string? KB { get; set; }
        public bool Security { get; set; }
    }
}