#!/usr/bin/env -S dotnet --
#:package StackExchange.Redis@2.*
#:property PublishAot=false
#:include connection.cs

// §4.1 — the single-instance Redis lock, and why unlock must be
// compare-and-delete rather than DEL.
//   dotnet run 06-redis-lock.cs             correct unlock
//   dotnet run 06-redis-lock.cs -- --naive  unlock with DEL, and watch it break

using StackExchange.Redis;

var naive = args.Contains("--naive");
const string Key = "lock:order:123";
var ttl = TimeSpan.FromSeconds(2);

var redis = await ConnectionMultiplexer.ConnectAsync(Conn.Redis);
var db = redis.GetDatabase();
await db.KeyDeleteAsync(Key);

Console.WriteLine($"redis  -> {Conn.Redis}");
Console.WriteLine($"unlock -> {(naive ? "DEL  (the naive, broken way)" : "compare-and-delete (correct)")}\n");

// ---- basic mutual exclusion -------------------------------------------------
var tokenA = Guid.NewGuid().ToString("n");
Console.WriteLine($"A: SET {Key} <tokenA> NX PX {ttl.TotalMilliseconds}");
Console.WriteLine($"A: acquired = {await TryAcquire(db, Key, tokenA, ttl)}");

var tokenB = Guid.NewGuid().ToString("n");
Console.WriteLine($"B: acquired = {await TryAcquire(db, Key, tokenB, ttl)}   <-- refused, A holds it\n");

// ---- the interleaving that breaks DEL ---------------------------------------
Console.WriteLine("Now the timeline that matters:\n");

Console.WriteLine($"  t=0.0  A holds the lock, TTL {ttl.TotalSeconds}s");
Console.WriteLine( "  t=0.0  A stalls — GC pause, VM deschedule, slow downstream call");
await Task.Delay(ttl + TimeSpan.FromMilliseconds(600));
Console.WriteLine($"  t={ttl.TotalSeconds + 0.6:0.0}  ...A is still stalled. The lock has EXPIRED.");

var tokenC = Guid.NewGuid().ToString("n");
var bGot = await TryAcquire(db, Key, tokenC, ttl);
Console.WriteLine($"  t={ttl.TotalSeconds + 0.6:0.0}  B acquires the now-free lock: {bGot}");

Console.WriteLine( "  t=?    A finally wakes up and releases...");
var released = naive
    ? await db.KeyDeleteAsync(Key)                     // the bug
    : await CompareAndDelete(db, Key, tokenA);         // the fix

var whoHoldsIt = await db.StringGetAsync(Key);
var bStillHolds = whoHoldsIt == tokenC;

Console.WriteLine($"         A's release removed a key: {released}");
Console.WriteLine($"         does B still hold the lock? {(bStillHolds ? "yes" : "NO — A deleted it")}\n");

if (bStillHolds)
    Console.WriteLine("""
        Correct. A's token did not match, so the compare-and-delete was a
        no-op. A had already lost the lock and it knew not to touch it.
        """);
else
    Console.WriteLine("""
        There it is. A deleted a lock it no longer held.

        B is still running, believing it has mutual exclusion. The lock is
        now free, so C can take it too, and B and C run concurrently over
        the same resource. Plain DEL does not merely fail to protect you --
        it actively breaks the NEXT holder.

        Re-run without --naive to see the compare-and-delete refuse.
        """);

await redis.CloseAsync();

static async Task<bool> TryAcquire(IDatabase db, string key, string token, TimeSpan ttl) =>
    await db.StringSetAsync(key, token, ttl, When.NotExists);

static async Task<bool> CompareAndDelete(IDatabase db, string key, string token)
{
    // The canonical unlock. Redis 8.4+ also offers: DELEX key IFEQ token
    const string lua = """
        if redis.call("get", KEYS[1]) == ARGV[1]
          then return redis.call("del", KEYS[1])
          else return 0 end
        """;
    var n = (int)(await db.ScriptEvaluateAsync(lua, [key], [token]));
    return n == 1;
}
