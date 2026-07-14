using System.Diagnostics;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Reports the top 25 processes by CPU time using System.Diagnostics.Process.
/// Note: Process.TotalProcessorTime is cumulative, not a current CPU-percent
/// figure. A true CPU% requires two samples separated by an interval (the same
/// way /proc/stat works). We report total CPU time here, which is useful for
/// identifying high-CPU processes over time even without the rate calculation.
/// The derivation engine can compute the rate from successive facts.
/// </summary>
public sealed class ProcessCollector : ILocalCollector
{
    public string Name => "process";
    public bool IsSupported => true;

    public Task<IReadOnlyList<Fact>> CollectAsync(string deviceId, CancellationToken ct)
    {
        List<Fact> facts = new();

        var processes = Process.GetProcesses()
            .Select(p =>
                {
                    try
                    {
                        return new
                        {
                            p.Id,
                            p.ProcessName,
                            CpuTime = p.TotalProcessorTime.TotalSeconds,
                            MemBytes = p.WorkingSet64,
                        };
                    }
                    catch (Exception)
                    {
                        return null; // access denied on some system processes — skip
                    }
                }
            )
            .Where(p => p is not null)
            .OrderByDescending(p => p?.CpuTime)
            .Take(25)
            .ToList();

        foreach (var proc in processes)
        {
            if (proc is null)
            {
                continue;
            }

            string[] keys = [deviceId, proc.Id.ToString()];
            facts.Add(Fact.Create(FactPaths.ProcessName, keys, proc.ProcessName));
            facts.Add(Fact.Create(FactPaths.ProcessCpuTimeSecs, keys, proc.CpuTime));
            facts.Add(Fact.Create(FactPaths.ProcessMemBytes, keys, proc.MemBytes));
        }

        return Task.FromResult<IReadOnlyList<Fact>>(facts);
    }
}