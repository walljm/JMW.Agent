using System.Runtime.Versioning;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects user-related facts: active login sessions (who) and local accounts
/// (/etc/passwd), enriched with admin group membership (/etc/group).
/// System accounts (UID &lt; 1000) are skipped except root (UID 0).
/// Admin is defined as membership in the sudo, wheel, or admin group.
/// </summary>
public sealed class UserCollector : OsDispatchLocalCollector
{
    public override string Name => "user";

    // ── Linux ─────────────────────────────────────────────────────────────────

    protected override async Task CollectLinuxAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        await CollectActiveSessionsAsync(deviceId, facts, ct);
        CollectLocalAccounts(deviceId, facts);
    }

    private static async Task CollectActiveSessionsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        try
        {
            string output = await CollectorHelper.RunAsync("who", "", ct);

            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // who output: user  tty  date  time  [(host)]
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                string user = parts[0];
                string tty = parts[1];
                string sessionKey = $"{user}@{tty}";

                facts.Add(Fact.Create(FactPaths.SessionUser, [deviceId, sessionKey], user));
                facts.Add(Fact.Create(FactPaths.SessionTty, [deviceId, sessionKey], tty));

                if (parts.Length >= 4)
                {
                    facts.Add(Fact.Create(FactPaths.SessionLoginAt, [deviceId, sessionKey], $"{parts[2]} {parts[3]}"));
                }

                // Optional remote host in parens at end: (192.168.1.5)
                string last = parts[^1];
                if (last.StartsWith('(') && last.EndsWith(')'))
                {
                    facts.Add(Fact.Create(FactPaths.SessionHost, [deviceId, sessionKey], last.Trim('(', ')')));
                }
            }
        }
        catch { }
    }

    private static void CollectLocalAccounts(string deviceId, List<Fact> facts)
    {
        HashSet<string> adminUsers = ReadAdminGroupMembers();

        try
        {
            foreach (string line in File.ReadLines("/etc/passwd"))
            {
                string[] fields = line.Split(':');
                if (fields.Length < 7)
                {
                    continue;
                }

                if (!int.TryParse(fields[2], out int uid))
                {
                    continue;
                }

                // Keep root and human accounts (UID >= 1000); skip system accounts.
                if (uid != 0 && uid < 1000)
                {
                    continue;
                }

                string name = fields[0];

                facts.Add(Fact.Create(FactPaths.LocalUserUsername, [deviceId, name], name));
                facts.Add(Fact.Create(FactPaths.LocalUserUid, [deviceId, name], uid));
                facts.Add(Fact.Create(FactPaths.LocalUserGid, [deviceId, name], fields[3]));
                facts.Add(Fact.Create(FactPaths.LocalUserHome, [deviceId, name], fields[5]));
                facts.Add(Fact.Create(FactPaths.LocalUserShell, [deviceId, name], fields[6]));
                facts.Add(Fact.Create(FactPaths.LocalUserIsAdmin, [deviceId, name], adminUsers.Contains(name)));
            }
        }
        catch { }
    }

    // ── macOS ─────────────────────────────────────────────────────────────────

    protected override async Task CollectMacOsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // Active sessions: same `who` output as Linux
        await CollectActiveSessionsAsync(deviceId, facts, ct);

        // Local accounts via dscl (Directory Service command line utility)
        // `dscl . list /Users UniqueID` gives username + UID pairs
        string output = await CollectorHelper.RunAsync("dscl", ". list /Users UniqueID", ct);
        HashSet<string> adminMembers = await GetDarwinAdminMembersAsync(ct);

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            if (!int.TryParse(parts[1], out int uid))
            {
                continue;
            }

            if (uid != 0 && uid < 500)
            {
                continue; // skip system accounts (macOS uses 500 threshold)
            }

            string name = parts[0];
            facts.Add(Fact.Create(FactPaths.LocalUserUsername, [deviceId, name], name));
            facts.Add(Fact.Create(FactPaths.LocalUserUid, [deviceId, name], uid));
            facts.Add(Fact.Create(FactPaths.LocalUserIsAdmin, [deviceId, name], adminMembers.Contains(name)));
        }
    }

    private static async Task<HashSet<string>> GetDarwinAdminMembersAsync(CancellationToken ct)
    {
        HashSet<string> admins = new(StringComparer.Ordinal);
        string output = await CollectorHelper.RunAsync("dscl", ". read /Groups/admin GroupMembership", ct);
        foreach (string line in output.Split('\n'))
        {
            if (!line.StartsWith("GroupMembership:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string member in line["GroupMembership:".Length..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                admins.Add(member.Trim());
            }
        }

        return admins;
    }

    // ── Windows ───────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    protected override async Task CollectWindowsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // Active sessions via quser (query user)
        string sessions = await CollectorHelper.RunPsAsync(
            @"try { (quser 2>&1) -join ""`n"" } catch { '' }",
            ct
        );

        string[] lines = sessions.Split('\n');
        foreach (string line in lines.Skip(1)) // skip header
        {
            string[] parts = line.TrimStart('>').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            string user = parts[0];
            string tty = parts[1];
            string sessionKey = $"{user}@{tty}";
            facts.Add(Fact.Create(FactPaths.SessionUser, [deviceId, sessionKey], user));
            facts.Add(Fact.Create(FactPaths.SessionTty, [deviceId, sessionKey], tty));
        }

        // Local accounts via Get-LocalUser + admin group membership
        const string script = """
            $admins = @{}
            try {
              Get-LocalGroupMember -Group 'Administrators' -ErrorAction Stop | ForEach-Object {
                $admins[($_.Name -split '\\')[-1]] = $true
              }
            } catch {}
            Get-LocalUser | ForEach-Object {
              [pscustomobject]@{
                Name      = $_.Name
                SID       = $_.SID.Value
                Disabled  = -not $_.Enabled
                IsAdmin   = $admins.ContainsKey($_.Name)
              }
            } | ConvertTo-Json -Compress -Depth 3
            """;

        List<LocalUserRow> rows = await CollectorHelper.RunPsJsonAsync<LocalUserRow>(script, ct);
        foreach (LocalUserRow r in rows)
        {
            if (r.Name is null)
            {
                continue;
            }

            facts.Add(Fact.Create(FactPaths.LocalUserUsername, [deviceId, r.Name], r.Name));
            facts.Add(Fact.Create(FactPaths.LocalUserUid, [deviceId, r.Name], r.SID ?? ""));
            facts.Add(Fact.Create(FactPaths.LocalUserIsAdmin, [deviceId, r.Name], r.IsAdmin));
            facts.Add(Fact.Create(FactPaths.LocalUserDisabled, [deviceId, r.Name], r.Disabled));
        }
    }

    private sealed class LocalUserRow
    {
        public string? Name { get; set; }
        public string? SID { get; set; }
        public bool Disabled { get; set; }
        public bool IsAdmin { get; set; }
    }

    // Returns set of usernames that are members of sudo, wheel, or admin groups.
    private static HashSet<string> ReadAdminGroupMembers()
    {
        HashSet<string> admins = new(StringComparer.Ordinal);
        try
        {
            foreach (string line in File.ReadLines("/etc/group"))
            {
                string[] fields = line.Split(':');
                if (fields.Length < 4)
                {
                    continue;
                }

                if (fields[0] is not ("sudo" or "wheel" or "admin"))
                {
                    continue;
                }

                foreach (string member in fields[3].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    admins.Add(member.Trim());
                }
            }
        }
        catch { }

        return admins;
    }
}