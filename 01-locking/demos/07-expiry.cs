#!/usr/bin/env -S dotnet --
#:package StackExchange.Redis@2.*
#:property PublishAot=false
#:include connection.cs

// §4 — the money shot.
//
// The lock is correct. SET NX PX, unique token, compare-and-delete unlock.
// Everything the docs tell you to do. And the invariant still breaks, because
// a distributed lock has a TTL and the TTL can expire WHILE YOU ARE WORKING.
//
//   dotnet run 07-expiry.cs             broken, as designed
//   dotnet run 07-expiry.cs -- --fence  the resource rejects stale writers
//   dotnet run 07-expiry.cs -- --ttl 5  widen the lease, lose less often

using StackExchange.Redis;

var fence = args.Contains("--fence");
var ttlSec = GetArg("--ttl", 2);
var workers = GetArg("--workers", 8);
var ttl = TimeSpan.FromSeconds(ttlSec);

const string Lock = "demo:lock";
const string Counter = "demo:counter";
const string Fence = "demo:fence";
const string Seq = "demo:seq";

var redis = await ConnectionMultiplexer.ConnectAsync(Conn.Redis);
var db = redis.GetDatabase();
await db.KeyDeleteAsync([Lock, Counter, Fence, Seq]);
await db.StringSetAsync(Counter, 0);

Console.WriteLine($"{workers} workers, lease TTL {ttlSec}s, fencing {(fence ? "ON" : "OFF")}");
Console.WriteLine($"each worker: take the lock, read the counter, work, write counter+1\n");

var rejected = 0;
await Task.WhenAll(Enumerable.Range(1, workers).Select(async id =>
{
    await Task.Delay(id * 120);
    var token = Guid.NewGuid().ToString("n");

    while (!await db.StringSetAsync(Lock, token, ttl, When.NotExists))
        await Task.Delay(100);

    // A fencing token is handed out WITH the lock, and it only ever goes up.
    var fenceToken = await db.StringIncrementAsync(Seq);

    var read = (int)await db.StringGetAsync(Counter);

    // Simulate the pause that ruins everything: GC, VM deschedule, a slow
    // downstream call. Half the workers overrun their own lease.
    var overrun = id % 2 == 0;
    var work = overrun ? ttl + TimeSpan.FromMilliseconds(700) : TimeSpan.FromMilliseconds(200);
    Console.WriteLine($"  w{id}: lock (fence {fenceToken}), read {read}, work {work.TotalSeconds:0.0}s"
                    + (overrun ? "   <-- will overrun the lease" : ""));
    await Task.Delay(work);

    var ok = fence
        ? await FencedWrite(db, read + 1, fenceToken)
        : await BlindWrite(db, read + 1);

    if (!ok) Interlocked.Increment(ref rejected);
    Console.WriteLine($"  w{id}: write {read + 1} -> {(ok ? "accepted" : "REJECTED (stale fence)")}");

    await CompareAndDelete(db, Lock, token);
}));

var final = (int)await db.StringGetAsync(Counter);
Console.WriteLine($"\n  expected counter: {workers}");
Console.WriteLine($"  actual counter:   {final}");
Console.WriteLine($"  lost updates:     {workers - final}");
if (fence) Console.WriteLine($"  writes rejected:  {rejected}");

Console.WriteLine(fence
    ? """

      The counter is still short -- and that is CORRECT.

      Every rejected write was a worker whose lease had expired. Without
      fencing it would have clobbered a newer value. The resource refused
      it, so no update was silently lost; the work simply needs retrying.

      Note what had to be true: the RESOURCE had to check the token. That
      is the whole idea, and the whole practical objection -- you cannot
      fence an email, an API call, or a payment.
      """
    : """

      Every worker held the lock, correctly, by the book. And updates were
      still lost.

      The overrunning workers had their lease expire mid-work. Someone else
      took the lock and incremented. Then the original woke up and wrote a
      value computed from a counter that was already stale.

      No lock fixes this, because the lock was never broken. Re-run with
      --fence to see the resource reject the stale writers.
      """);

await redis.CloseAsync();

int GetArg(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

static async Task<bool> BlindWrite(IDatabase db, int value)
{
    await db.StringSetAsync(Counter, value);
    return true;
}

static async Task<bool> FencedWrite(IDatabase db, int value, long token)
{
    // The RESOURCE remembers the highest token it has seen and refuses
    // anything older. This is what makes a fencing token a fencing token.
    const string lua = """
        local seen = tonumber(redis.call("get", KEYS[2]) or "0")
        if tonumber(ARGV[2]) < seen then return 0 end
        redis.call("set", KEYS[2], ARGV[2])
        redis.call("set", KEYS[1], ARGV[1])
        return 1
        """;
    return (int)(await db.ScriptEvaluateAsync(lua, [Counter, Fence], [value, token])) == 1;
}

static async Task<bool> CompareAndDelete(IDatabase db, string key, string token)
{
    const string lua = """
        if redis.call("get", KEYS[1]) == ARGV[1]
          then return redis.call("del", KEYS[1]) else return 0 end
        """;
    return (int)(await db.ScriptEvaluateAsync(lua, [key], [token])) == 1;
}
