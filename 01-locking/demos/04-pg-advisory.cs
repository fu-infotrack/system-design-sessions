#!/usr/bin/env -S dotnet --
#:package Npgsql@9.*
#:property PublishAot=false
#:include connection.cs

// §3.3 — session-scoped vs transaction-scoped advisory locks.
// Connects DIRECTLY to Postgres (no PgBouncer). For the pooling disaster,
// see 04b-pgbouncer-leak.cs.
//   dotnet run 04-pg-advisory.cs

using Npgsql;

const int CmdTimeoutSec = 3;   // fail fast on stage instead of hanging

Console.WriteLine($"direct -> {Conn.Postgres}\n");

await Scenario("pg_advisory_lock  (SESSION scope)", "pg_advisory_lock", 42);
Console.WriteLine();
await Scenario("pg_advisory_xact_lock  (TRANSACTION scope)", "pg_advisory_xact_lock", 43);

Console.WriteLine("""

    The difference in one line:

      pg_advisory_lock       survives COMMIT. Released only by an explicit
                             unlock, or when the CONNECTION closes.
      pg_advisory_xact_lock  released by COMMIT or ROLLBACK. Always. You
                             cannot forget to unlock it.

    That is why the transaction-scoped form is the default you want. A
    session lock outlives the work it was protecting, and on a pooled
    connection it outlives the REQUEST -- see 10-efcore-pooling.cs.
    """);

static async Task Scenario(string title, string fn, long Key)
{
    Console.WriteLine($"=== {title} ===");

    await using var a = new NpgsqlConnection(Conn.Postgres);
    await using var observer = new NpgsqlConnection(Conn.Postgres);
    await a.OpenAsync();
    await observer.OpenAsync();

    await using (var tx = await a.BeginTransactionAsync())
    {
        await Exec(a, $"select {fn}({Key})", tx);
        Console.WriteLine($"  A: took {fn}({Key}) inside a transaction");
        Console.WriteLine($"     advisory locks visible: {await CountLocks(observer)}");
        await tx.CommitAsync();
    }

    Console.WriteLine("  A: COMMIT");
    Console.WriteLine($"     advisory locks visible: {await CountLocks(observer)}   <-- the tell");

    var free = await CanTake(Conn.Postgres, Key);
    Console.WriteLine($"  C: a NEW connection can take the lock? {(free ? "YES" : "no — still held")}");

    await a.CloseAsync();
    await Task.Delay(150);
    Console.WriteLine($"  A: disconnected");
    Console.WriteLine($"     advisory locks visible: {await CountLocks(observer)}");
}

static async Task<int> CountLocks(NpgsqlConnection c)
{
    await using var cmd = new NpgsqlCommand(
        "select count(*) from pg_locks where locktype = 'advisory'", c);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync());
}

static async Task<bool> CanTake(string cs, long key)
{
    await using var c = new NpgsqlConnection(cs);
    await c.OpenAsync();
    await using var cmd = new NpgsqlCommand($"select pg_try_advisory_lock({key})", c);
    var got = (bool)(await cmd.ExecuteScalarAsync())!;
    if (got) await new NpgsqlCommand($"select pg_advisory_unlock({key})", c).ExecuteNonQueryAsync();
    return got;
}

static async Task Exec(NpgsqlConnection c, string sql, NpgsqlTransaction? tx = null)
{
    await using var cmd = new NpgsqlCommand(sql, c, tx);
    cmd.CommandTimeout = CmdTimeoutSec;
    await cmd.ExecuteNonQueryAsync();
}
