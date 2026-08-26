#!/usr/bin/env -S dotnet --
#:property PublishAot=false

// §1 Demo 1 — the counter.
// Eight threads, the same +1, four ways.
//   dotnet run 01-counter.cs

using System.Diagnostics;

const int Threads = 8;
const int PerThread = 200_000;
const int Expected = Threads * PerThread;
const int RaceRuns = 5;

Console.WriteLine($"{Threads} threads x {PerThread:N0} increments");
Console.WriteLine($"expected: {Expected:N0}\n");

// The race is run several times on purpose: it does NOT always lose.
// That unreliability is the lesson — a race that passes in dev and in CI
// is still a race.
Console.WriteLine("  no lock");
var wrong = 0;
for (var run = 1; run <= RaceRuns; run++)
{
    var (value, _) = Race();
    var ok = value == Expected;
    if (!ok) wrong++;
    Console.WriteLine($"      run {run}   {value,12:N0}   {(ok ? "ok  <- got lucky" : "WRONG")}");
}
Console.WriteLine($"      -> wrong on {wrong} of {RaceRuns} runs\n");

Report("Interlocked", ViaInterlocked());
Report("lock", ViaLock());
Report("Lock (.NET 9)", ViaNewLock());

Console.WriteLine("""

    Two things to notice.

    1. The unlocked run is a different number every time, and sometimes it is
       the RIGHT number. A race you cannot reproduce is still a race.

    2. `Lock` is not obviously faster than `Monitor` here. The widely repeated
       "~25% faster" figure has no Microsoft source. This is one contended
       microbenchmark on one machine -- if the difference matters to you,
       measure your own workload with BenchmarkDotNet.
    """);

static (long Value, long Ms) Race()
{
    long count = 0;
    var ms = Time(() => count++);
    return (count, ms);
}

static (long Value, long Ms) ViaInterlocked()
{
    long count = 0;
    var ms = Time(() => Interlocked.Increment(ref count));
    return (count, ms);
}

static (long Value, long Ms) ViaLock()
{
    long count = 0;
    var gate = new object();                 // Monitor
    var ms = Time(() => { lock (gate) count++; });
    return (count, ms);
}

static (long Value, long Ms) ViaNewLock()
{
    long count = 0;
    var gate = new Lock();                   // System.Threading.Lock, .NET 9+
    var ms = Time(() => { lock (gate) count++; });
    return (count, ms);
}

static long Time(Action increment)
{
    var sw = Stopwatch.StartNew();
    var threads = new Thread[Threads];
    for (var t = 0; t < Threads; t++)
    {
        threads[t] = new Thread(() =>
        {
            for (var i = 0; i < PerThread; i++) increment();
        });
        threads[t].Start();
    }
    foreach (var thread in threads) thread.Join();
    return sw.ElapsedMilliseconds;
}

static void Report(string name, (long Value, long Ms) r)
{
    var verdict = r.Value == Expected ? "ok" : $"WRONG (lost {Expected - r.Value:N0})";
    Console.WriteLine($"  {name,-14} {r.Value,12:N0}  {r.Ms,5} ms   {verdict}");
}
