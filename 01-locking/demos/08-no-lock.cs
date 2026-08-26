#!/usr/bin/env -S dotnet --
#:package Npgsql@9.*
#:property PublishAot=false
#:include connection.cs

// Step 1 of the framework — let the database enforce the invariant.
// No lock, no coordination, no TTL. Two writers race; the DB picks a winner
// and the loser gets a deterministic error to handle.
//   dotnet run 08-no-lock.cs

using Npgsql;

const int Racers = 8;

await Setup();
await CheckThenInsert();     // the race, with no constraint
await UniqueConstraint();    // "only once"
await ExclusionConstraint(); // "no overlap"

Console.WriteLine("""

    All three used the same number of concurrent writers.

    The difference is not the application code -- it is whether the INVARIANT
    was written down in the schema. Once it is, the database enforces it
    against every writer, including ones that don't know the rule exists:
    a migration, another service, someone in psql at 2am.

    A lock only binds the code that remembers to take it.
    """);

// ---------------------------------------------------------------------------
static async Task CheckThenInsert()
{
    Console.WriteLine("=== 1. check-then-insert, NO constraint ===");
    var inserted = 0;
    await Race(async (c, i) =>
    {
        await using var check = new NpgsqlCommand(
            "select count(*) from payments_unsafe where order_id = 123", c);
        if (Convert.ToInt64(await check.ExecuteScalarAsync()) > 0) return;

        await Task.Delay(20);                       // the window every race needs
        await using var ins = new NpgsqlCommand(
            $"insert into payments_unsafe (order_id, racer) values (123, {i})", c);
        await ins.ExecuteNonQueryAsync();
        Interlocked.Increment(ref inserted);
    });

    var rows = await Scalar("select count(*) from payments_unsafe");
    Console.WriteLine($"  {Racers} racers -> {rows} rows   {(rows == 1 ? "" : "<-- the customer was charged " + rows + " times")}\n");
}

static async Task UniqueConstraint()
{
    Console.WriteLine("=== 2. unique index + ON CONFLICT DO NOTHING ===");
    var won = 0;
    await Race(async (c, i) =>
    {
        await using var cmd = new NpgsqlCommand(
            $"insert into payments (order_id, racer) values (123, {i}) on conflict do nothing", c);
        if (await cmd.ExecuteNonQueryAsync() == 1) Interlocked.Increment(ref won);
    });

    var rows = await Scalar("select count(*) from payments");
    Console.WriteLine($"  {Racers} racers -> {rows} row, {won} winner, {Racers - won} no-ops");
    Console.WriteLine( "  no lock, no retry, no coordination\n");
}

static async Task ExclusionConstraint()
{
    Console.WriteLine("=== 3. EXCLUDE constraint — no overlapping bookings ===");
    var won = 0; var rejected = 0; var deadlocked = 0;
    await Race(async (c, i) =>
    {
        // every racer wants an overlapping slot in the same room
        var start = $"2026-09-01 10:{i:00}";
        try
        {
            await using var cmd = new NpgsqlCommand(
                $"insert into bookings (room_id, during) values " +
                $"(1, tstzrange('{start}', '{start}'::timestamptz + interval '1 hour'))", c);
            await cmd.ExecuteNonQueryAsync();
            Interlocked.Increment(ref won);
        }
        catch (PostgresException e) when (e.SqlState == "23P01")   // exclusion_violation
        {
            Interlocked.Increment(ref rejected);
        }
        catch (PostgresException e) when (e.SqlState == "40P01")   // deadlock_detected
        {
            // Real behaviour, worth knowing: the constraint check takes locks,
            // so N mutually-overlapping concurrent inserts CAN deadlock.
            // Postgres picks a victim and aborts it. Retry is the answer.
            Interlocked.Increment(ref deadlocked);
        }
    });

    var rows = await Scalar("select count(*) from bookings");
    Console.WriteLine($"  {Racers} racers -> {rows} booking, {won} winner, "
                    + $"{rejected} rejected 23P01, {deadlocked} deadlocked 40P01");
    Console.WriteLine( "  \"no two bookings may overlap\" enforced declaratively");
    if (deadlocked > 0)
        Console.WriteLine("""
      Note the 40P01s. The exclusion check takes locks internally, so
      mutually-conflicting concurrent inserts can deadlock; Postgres aborts
      a victim. The invariant still held -- exactly one booking exists -- but
      a real caller must retry on 40P01 as well as handle 23P01.
    """);
    Console.WriteLine();
}

// ---------------------------------------------------------------------------
static async Task Race(Func<NpgsqlConnection, int, Task> body)
{
    using var gate = new SemaphoreSlim(0, Racers);
    var tasks = Enumerable.Range(1, Racers).Select(async i =>
    {
        await using var c = new NpgsqlConnection(Conn.Postgres);
        await c.OpenAsync();
        await gate.WaitAsync();                     // line everyone up first
        try { await body(c, i); } catch (PostgresException e) when (e.SqlState == "23505") { }
    }).ToArray();

    await Task.Delay(300);
    gate.Release(Racers);                           // ...then release together
    await Task.WhenAll(tasks);
}

static async Task<long> Scalar(string sql)
{
    await using var c = new NpgsqlConnection(Conn.Postgres);
    await c.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, c);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync());
}

static async Task Setup()
{
    await using var c = new NpgsqlConnection(Conn.Postgres);
    await c.OpenAsync();
    foreach (var sql in new[]
    {
        "create extension if not exists btree_gist",
        "drop table if exists payments_unsafe, payments, bookings",
        "create table payments_unsafe (order_id int, racer int)",
        "create table payments (order_id int, racer int)",
        "create unique index on payments (order_id)",
        "create table bookings (room_id int not null, during tstzrange not null, " +
            "exclude using gist (room_id with =, during with &&))",
    })
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}
