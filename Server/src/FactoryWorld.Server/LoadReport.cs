using System.Diagnostics;
using FactoryWorld.Modules.Abstractions;

namespace FactoryWorld.Server;

/// <summary>
/// Once a minute writes to the log how busy the server was: its CPU, how many messages it answered and how long
/// they took, with the kinds that took the most time in all. When the game feels slow, this says whether the
/// server is the reason and what keeps it busy.
/// </summary>
public sealed class LoadReport(ServerLoad load, ILogger<LoadReport> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    /// <summary>How many of the busiest kinds of message a report names.</summary>
    private const int Shown = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var at = Stopwatch.GetTimestamp();
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            process.Refresh();
            var used = process.TotalProcessorTime - cpu;
            var span = Stopwatch.GetElapsedTime(at);
            (cpu, at) = (process.TotalProcessorTime, Stopwatch.GetTimestamp());
            var tallies = load.Take();
            if (tallies.Count == 0)
            {
                continue;
            }

            var busiest = string.Join("; ", tallies.Take(Shown).Select(t =>
                $"{t.Type} {t.Count}x {t.Total.TotalMilliseconds / t.Count:0.#} ms avg {t.Max.TotalMilliseconds:0} ms max"));
            logger.LogInformation(
                "Load in the last minute: CPU {Cpu:0}% of {Cores} cores, {Messages} messages, {Seconds:0.0} s spent on them; busiest: {Busiest}",
                used / span * 100 / Environment.ProcessorCount, Environment.ProcessorCount,
                tallies.Sum(t => t.Count), tallies.Sum(t => t.Total.TotalSeconds), busiest);
        }
    }
}
