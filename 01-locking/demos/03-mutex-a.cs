#!/usr/bin/env -S dotnet --
#:property PublishAot=false

// §1b — named Mutex, the HOLDER.
//   dotnet run 03-mutex-a.cs            hold until Enter
//   dotnet run 03-mutex-a.cs -- 10      hold 10 seconds (for scripting)

var name = Environment.GetEnvironmentVariable("MUTEX_NAME") ?? "OneTrueLock";
var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : -1;

Console.WriteLine($"[A] pid {Environment.ProcessId}, sid {Sid()}, mutex \"{name}\"");

using var mutex = new Mutex(false, name);
Console.WriteLine("[A] acquiring...");
mutex.WaitOne();
Console.WriteLine("[A] ACQUIRED");
ShowBackingFiles();

if (seconds < 0) { Console.WriteLine("[A] holding — press Enter to release"); Console.ReadLine(); }
else { Console.WriteLine($"[A] holding {seconds}s"); Thread.Sleep(seconds * 1000); }

mutex.ReleaseMutex();
Console.WriteLine("[A] released");

static string Sid()
{
    try { return File.ReadAllText($"/proc/{Environment.ProcessId}/stat").Split(' ')[5]; }
    catch { return "n/a"; }
}

static void ShowBackingFiles()
{
    // On Unix .NET backs named mutexes with files. Seeing them is the point.
    var root = "/tmp/.dotnet/shm";
    if (!Directory.Exists(root)) { Console.WriteLine($"[A] (no {root})"); return; }
    Console.WriteLine($"[A] backing files under {root}:");
    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        Console.WriteLine($"[A]   {f}");
}
