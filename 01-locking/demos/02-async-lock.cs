#!/usr/bin/env -S dotnet --
#:property PublishAot=false

// §1.2 Demo 2 — SemaphoreSlim: the async mutex and its two footguns.
//   dotnet run 02-async-lock.cs

// ---------------------------------------------------------------- 0. why not lock
//
//   lock (gate) { await Task.Delay(10); }   // CS1996 - cannot await in the
//                                           // body of a lock statement
//
// Monitor ownership is bound to the THREAD. A continuation can resume on a
// different thread, so there would be nobody to release it. Hence SemaphoreSlim.
//
// Aside: `lock (myLock) { }` inside an `async` method compiles FINE as long as
// there is no `await` in the body. Microsoft Learn claims CS9217 covers this;
// it does not -- CS9217 is ERR_RefLocalAcrossAwait. See research/01.

Console.WriteLine("=== 1. SemaphoreSlim as an async mutex — works ===\n");
{
    var gate = new SemaphoreSlim(1, 1);
    var log = new List<string>();
    var inside = 0;
    var maxSeen = 0;

    await Task.WhenAll(Enumerable.Range(1, 5).Select(async id =>
    {
        await gate.WaitAsync();
        try
        {
            var now = Interlocked.Increment(ref inside);
            maxSeen = Math.Max(maxSeen, now);
            lock (log) log.Add($"  worker {id} in");
            await Task.Delay(50);                  // the await lock cannot do
            lock (log) log.Add($"  worker {id} out");
            Interlocked.Decrement(ref inside);
        }
        finally { gate.Release(); }
    }));

    foreach (var line in log) Console.WriteLine(line);
    Console.WriteLine($"\n  max concurrent inside: {maxSeen}   (want 1)\n");
}

// ---------------------------------------------------------------- 2. not reentrant
Console.WriteLine("=== 2. Not reentrant — a recursive call self-deadlocks ===\n");
{
    var gate = new SemaphoreSlim(1, 1);

    async Task Recurse(int depth)
    {
        Console.WriteLine($"  entering depth {depth}...");
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Console.WriteLine($"  depth {depth}: DEADLOCK (timed out after 1s)");
            return;
        }
        try
        {
            if (depth < 2) await Recurse(depth + 1);
        }
        finally { gate.Release(); }
    }

    await Recurse(1);
    Console.WriteLine("\n  `lock` would have been FINE here — Monitor is reentrant.");
    Console.WriteLine("  SemaphoreSlim is a trade, not an upgrade.\n");
}

// ---------------------------------------------------------------- 3. the silent one
Console.WriteLine("=== 3. The footgun nobody knows: new SemaphoreSlim(1) ===\n");
{
    var sloppy = new SemaphoreSlim(1);        // maxCount defaults to int.MaxValue
    await sloppy.WaitAsync();
    sloppy.Release();
    sloppy.Release();                         // stray release. no exception.

    Console.WriteLine($"  after a stray Release(), CurrentCount = {sloppy.CurrentCount}");

    var inside = 0;
    var maxSeen = 0;
    await Task.WhenAll(Enumerable.Range(1, 4).Select(async _ =>
    {
        await sloppy.WaitAsync();
        try
        {
            var now = Interlocked.Increment(ref inside);
            maxSeen = Math.Max(maxSeen, now);
            await Task.Delay(80);
            Interlocked.Decrement(ref inside);
        }
        finally { sloppy.Release(); }
    }));

    Console.WriteLine($"  max concurrent inside a \"mutex\": {maxSeen}   <-- should be 1\n");

    var strict = new SemaphoreSlim(1, 1);     // two-arg: maxCount = 1
    await strict.WaitAsync();
    strict.Release();
    try
    {
        strict.Release();
        Console.WriteLine("  SemaphoreSlim(1,1): no exception?!");
    }
    catch (SemaphoreFullException)
    {
        Console.WriteLine("  SemaphoreSlim(1,1): threw SemaphoreFullException — good.");
    }
}

Console.WriteLine("""

    Always write `new SemaphoreSlim(1, 1)`.

    The one-arg constructor sets maxCount to int.MaxValue, so an extra
    Release() -- a double-release in a finally, a retry path, a copy-paste --
    silently RAISES your concurrency limit. Nothing throws. Your mutex
    quietly becomes a semaphore of 2, then 3, and the corruption it was
    guarding against starts happening.

    The two-arg form turns that silent failure into SemaphoreFullException.

    (For the record the exception is SemaphoreFullException.
     `SemaphoreMaxCountExceededException` does not exist.)
    """);
