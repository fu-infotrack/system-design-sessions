#!/usr/bin/env -S dotnet --
#:property PublishAot=false

// §1b — named Mutex, the CONTENDER. Reports whether it had to wait.
//   dotnet run 03-mutex-b.cs

var name = Environment.GetEnvironmentVariable("MUTEX_NAME") ?? "OneTrueLock";
var timeout = TimeSpan.FromSeconds(3);

Console.WriteLine($"[B] pid {Environment.ProcessId}, sid {Sid()}, mutex \"{name}\"");

using var mutex = new Mutex(false, name);
Console.WriteLine($"[B] trying for {timeout.TotalSeconds}s...");
var sw = System.Diagnostics.Stopwatch.StartNew();

bool got;
try { got = mutex.WaitOne(timeout); }
catch (AbandonedMutexException)
{
    Console.WriteLine("[B] AbandonedMutexException — previous holder died mid-work.");
    Console.WriteLine("[B] (on Linux this is often silently LOST — see research/07)");
    got = true;
}

sw.Stop();
if (got)
{
    var instant = sw.ElapsedMilliseconds < 250;
    Console.WriteLine($"[B] ACQUIRED after {sw.ElapsedMilliseconds} ms");
    Console.WriteLine(instant
        ? "[B]   -> INSTANT. No contention at all. Is that what you expected?"
        : "[B]   -> it waited, so it WAS contending, then the holder released.");
    mutex.ReleaseMutex();
}
else
{
    Console.WriteLine($"[B] BLOCKED for {sw.ElapsedMilliseconds} ms — contention. The mutex works.");
}

static string Sid()
{
    try { return File.ReadAllText($"/proc/{Environment.ProcessId}/stat").Split(' ')[5]; }
    catch { return "n/a"; }
}
