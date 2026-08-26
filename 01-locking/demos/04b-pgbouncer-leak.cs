#!/usr/bin/env -S dotnet --
#:package Npgsql@9.*
#:property PublishAot=false
#:include connection.cs

// §3.4 — the PgBouncer leak, at the infrastructure layer.
//
// Requires `aspire run` (PgBouncer in TRANSACTION pooling mode, pool size 1 --
// the pool size is what makes both clients land on the same backend, so you
// see the silent violation rather than a hang).
//   dotnet run 04b-pgbouncer-leak.cs

using Npgsql;

const long Key = 7777;

Console.WriteLine($"via PgBouncer -> {Conn.PgBouncer}\n");

await Cleanup();
await Leak("pg_advisory_lock", "SESSION");
Console.WriteLine();
await Cleanup();
await Leak("pg_advisory_xact_lock", "TRANSACTION");

Console.WriteLine("""

    PgBouncer's own feature matrix has exactly two mode columns and this row:

        Session-level advisory locks   |  Yes  |  Never

    The word "session-level" is doing the work -- transaction-scoped advisory
    locks are not listed as broken, and as you just saw, they aren't.

    The trap that hides it: server_reset_query still READS as "DISCARD ALL"
    via SHOW CONFIG in transaction mode. It simply never runs. Auditing the
    config tells you you are safe when you are not.
    """);

static async Task Leak(string fn, string scope)
{
    Console.WriteLine($"=== {fn}  ({scope} scope) through PgBouncer ===");

    // "Request 1" -- takes the lock, then finishes and goes away.
    await using (var a = new NpgsqlConnection(Conn.PgBouncer))
    {
        await a.OpenAsync();
        await using var tx = await a.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand($"select {fn}({Key})", a, tx);
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        Console.WriteLine($"  request 1: took {fn}({Key}), committed, disconnecting");
    }

    await Task.Delay(300);

    // THE TELL: what does the server hold now that request 1 is gone?
    var leaked = await AdvisoryCount();
    Console.WriteLine($"  server:    advisory locks on {Key} after request 1 = {leaked}"
                    + (leaked > 0 ? "   <-- LEAKED" : "   <-- released, clean"));

    // "Request 2" -- a completely unrelated request, later, same pool.
    await using (var b = new NpgsqlConnection(Conn.PgBouncer))
    {
        await b.OpenAsync();
        await using var cmd = new NpgsqlCommand($"select pg_try_advisory_lock({Key})", b);
        cmd.CommandTimeout = 5;
        var got = (bool)(await cmd.ExecuteScalarAsync())!;
        Console.WriteLine($"  request 2: pg_try_advisory_lock({Key}) -> {got.ToString().ToUpper()}");
        Console.WriteLine(leaked > 0 && got
            ? "             ^ MUTUAL EXCLUSION VIOLATED. Request 1's lock is still"
            + "\n               held on this very backend, and request 2 was told it"
            + "\n               acquired it too -- because session locks are STACKABLE."
            : "             ^ correct: the lock really was free.");
        await new NpgsqlCommand($"select pg_advisory_unlock_all()", b).ExecuteNonQueryAsync();
    }
}

static async Task<long> AdvisoryCount()
{
    await using var obs = new NpgsqlConnection(Conn.Postgres);
    await obs.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        $"select count(*) from pg_locks where locktype='advisory' and objid={Key}", obs);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync());
}

static async Task Cleanup()
{
    await using var c = new NpgsqlConnection(Conn.Postgres);
    await c.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "select pg_terminate_backend(pid) from pg_stat_activity " +
        "where pid <> pg_backend_pid() and application_name not like '%pgAdmin%'", c);
    try { await cmd.ExecuteNonQueryAsync(); } catch { }
    await Task.Delay(400);
}
