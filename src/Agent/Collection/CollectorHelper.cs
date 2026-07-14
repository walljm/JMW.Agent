using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Shared subprocess helpers used across local collectors.
/// All methods are best-effort: exceptions are caught and return empty/null.
/// </summary>
internal static class CollectorHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Unix subprocess ───────────────────────────────────────────────────────

    public static async Task<string> RunAsync(
        string cmd,
        string args,
        CancellationToken ct,
        int timeoutSeconds = 15
    )
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            ProcessStartInfo psi = new(cmd, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? proc = Process.Start(psi);
            string output =
                await (proc ?? throw new InvalidOperationException()).StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return output;
        }
        catch { return ""; }
    }

    /// <summary>
    /// Runs a subprocess and returns its exit code (stdout/stderr discarded), or -1 if it could
    /// not be started or timed out. For callers that key off exit status rather than output.
    /// </summary>
    public static async Task<int> RunForExitCodeAsync(
        string cmd,
        string args,
        CancellationToken ct,
        int timeoutSeconds = 15
    )
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            ProcessStartInfo psi = new(cmd, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? proc = Process.Start(psi);
            await (proc ?? throw new InvalidOperationException()).WaitForExitAsync(cts.Token);
            return proc.ExitCode;
        }
        catch { return -1; }
    }

    public static bool BinaryExists(string name)
    {
        try
        {
            ProcessStartInfo psi = new("which", name)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── Windows PowerShell ────────────────────────────────────────────────────

    // Run a PowerShell script, return trimmed stdout. Best-effort.
    [SupportedOSPlatform("windows")]
    public static async Task<string> RunPsAsync(string script, CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            ProcessStartInfo psi = new(
                "powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -"
            )
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process proc = Process.Start(psi)
             ?? throw new InvalidOperationException($"Failed to start process: {psi.FileName}");
            await proc.StandardInput.WriteLineAsync(script);
            proc.StandardInput.Close();

            string output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return output.Trim();
        }
        catch { return ""; }
    }

    // Run a PowerShell script that emits JSON and deserialize to T.
    // Handles the PowerShell single-item quirk: ConvertTo-Json emits an object
    // {} for one result and an array [{},{}] for multiple. When T is a list,
    // we wrap a bare object in [] before deserializing.
    [SupportedOSPlatform("windows")]
    public static async Task<List<T>> RunPsJsonAsync<T>(string script, CancellationToken ct)
    {
        string json = await RunPsAsync(script, ct);
        if (string.IsNullOrWhiteSpace(json) || json is "[]" or "null")
        {
            return [];
        }

        string trimmed = json.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            json = "[" + json + "]";
        }

        try { return JsonSerializer.Deserialize<List<T>>(json, JsonOpts) ?? []; }
        catch { return []; }
    }

    // Run a PowerShell script that emits a single JSON object and deserialize.
    [SupportedOSPlatform("windows")]
    public static async Task<T?> RunPsJsonOneAsync<T>(string script, CancellationToken ct)
        where T : class
    {
        string json = await RunPsAsync(script, ct);
        if (string.IsNullOrWhiteSpace(json) || json is "{}" or "null")
        {
            return null;
        }

        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch { return null; }
    }
}