#!/usr/bin/env -S dotnet --
#:package Npgsql@9.*
#:property PublishAot=false
#:include connection.cs

// Optimistic vs pessimistic concurrency control.
//
// A pessimistic lock stops the collision happening.
// Optimistic concurrency lets it happen and DETECTS it, then retries.
//   dotnet run 09-optimistic.cs

using Npgsql;

const int Workers = 8;
const int Deduct = 10;
const int Start = 100;

await Setup();

Console.WriteLine($"{Workers} workers, each deducting {Deduct} from a balance of {Start}");
Console.WriteLine($"correct answer: {Start - Workers * Deduct}\n");

await LostUpdate();
await Gotcha();
await Occ();
await ContentionSweep();

Console.WriteLine("""

    Optimistic concurrency is a fencing token applied to a database row.

    The version column IS the fence: the resource -- the row -- refuses a
    writer whose version is stale. That is the same mechanism as a fencing
    token in a distributed lock, except the resource is a table you own, so
    it can actually participate. This is why it works and Redlock's fencing
    usually can't.

    Use it when conflicts are RARE. It has no waiting, no deadlock, no lock
    to leak, and no TTL to expire. Under real contention it degrades into
    retry storms -- and then you want a pessimistic lock or a queue.
    """);

// ---------------------------------------------------------------------------
static async Task LostUpdate()
{
    Console.WriteLine("=== 1. read-modify-write, no version check ===");
    await Reset();
    await Race(async (c, i) =>
    {
        var (bal, _) = await Read(c);
        await Task.Delay(20);                       // the window
        await using var cmd = new NpgsqlCommand(
            $"update accounts set balance = {bal - Deduct} where id = 1", c);
        await cmd.ExecuteNonQueryAsync();
    });
    var final = await Balance();
    Console.WriteLine($"  final balance: {final}   <-- {(Start - final) / Deduct} of {Workers} deductions applied\n");
}

static async Task Gotcha()
{
    Console.WriteLine("=== 2. version column present, rows-affected IGNORED ===");
    await Reset();
    await Race(async (c, i) =>
    {
        var (bal, ver) = await Read(c);
        await Task.Delay(20);
        await using var cmd = new NpgsqlCommand(
            $"update accounts set balance = {bal - Deduct}, version = version + 1 " +
            $"where id = 1 and version = {ver}", c);
        await cmd.ExecuteNonQueryAsync();           // <-- return value discarded
    });
    var final = await Balance();
    Console.WriteLine($"  final balance: {final}   <-- {(Start - final) / Deduct} of {Workers} applied");
    Console.WriteLine("""
      The WHERE clause was correct. The version column was there. And it is
      exactly as broken as case 1, because an UPDATE matching zero rows is
      not an error in SQL -- it succeeds, quietly, having done nothing.

      Optimistic concurrency is the rows-affected check. Everything else is
      decoration.
    """);
}

static async Task<int> Occ(int workers = Workers, bool quiet = false)
{
    if (!quiet) Console.WriteLine("=== 3. optimistic concurrency, done properly ===");
    await Reset();
    var retries = 0;
    await Race(async (c, i) =>
    {
        while (true)
        {
            var (bal, ver) = await Read(c);
            await Task.Delay(20);
            await using var cmd = new NpgsqlCommand(
                $"update accounts set balance = {bal - Deduct}, version = version + 1 " +
                $"where id = 1 and version = {ver}", c);

            if (await cmd.ExecuteNonQueryAsync() == 1) return;   // won
            Interlocked.Increment(ref retries);                  // someone beat us
        }
    }, workers);

    if (!quiet)
    {
        Console.WriteLine($"  final balance: {await Balance()}   correct");
        Console.WriteLine($"  retries:       {retries}\n");
    }
    return retries;
}

static async Task ContentionSweep()
{
    Console.WriteLine("=== 4. when optimistic stops being the right answer ===");
    Console.WriteLine("  workers   retries   retries/worker");
    foreach (var n in new[] { 2, 4, 8, 16 })
    {
        var r = await Occ(n, quiet: true);
        Console.WriteLine($"    {n,3}       {r,4}        {(double)r / n:0.0}");
    }
    Console.WriteLine("""
      Retries per worker grows with contention. Optimistic concurrency is
      cheapest when conflicts are rare and gets worse exactly as conflicts
      get common -- the opposite of what you want under load.
    """);
}

// ---------------------------------------------------------------------------
static async Task Race(Func<NpgsqlConnection, int, Task> body, int workers = Workers)
{
    using var gate = new SemaphoreSlim(0, workers);
    var tasks = Enumerable.Range(1, workers).Select(async i =>
    {
        await using var c = new NpgsqlConnection(Conn.Postgres);
        await c.OpenAsync();
        await gate.WaitAsync();
        await body(c, i);
    }).ToArray();
    await Task.Delay(250);
    gate.Release(workers);
    await Task.WhenAll(tasks);
}

static async Task<(int Balance, int Version)> Read(NpgsqlConnection c)
{
    await using var cmd = new NpgsqlCommand("select balance, version from accounts where id = 1", c);
    await using var r = await cmd.ExecuteReaderAsync();
    await r.ReadAsync();
    return (r.GetInt32(0), r.GetInt32(1));
}

static async Task<int> Balance()
{
    await using var c = new NpgsqlConnection(Conn.Postgres);
    await c.OpenAsync();
    var (bal, _) = await Read(c);
    return bal;
}

static async Task Reset()
{
    await using var c = new NpgsqlConnection(Conn.Postgres);
    await c.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        $"update accounts set balance = {Start}, version = 0 where id = 1", c);
    await cmd.ExecuteNonQueryAsync();
}

static async Task Setup()
{
    await using var c = new NpgsqlConnection(Conn.Postgres);
    await c.OpenAsync();
    foreach (var sql in new[]
    {
        "drop table if exists accounts",
        "create table accounts (id int primary key, balance int not null, version int not null default 0)",
        $"insert into accounts (id, balance) values (1, {Start})",
    })
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}
