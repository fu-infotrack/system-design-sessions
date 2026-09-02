#!/usr/bin/env -S dotnet --
#:package Microsoft.EntityFrameworkCore@9.*
#:package Npgsql.EntityFrameworkCore.PostgreSQL@9.*
#:property PublishAot=false
#:include connection.cs

// §3.4 — advisory locks under EF Core.
//
// No PgBouncer anywhere. This is EF Core on top of Npgsql's connection pool,
// which is ON BY DEFAULT.
//
// One deliberate deviation from a real app: Maximum Pool Size=1. A real app
// defaults to 100, where the second request usually draws a DIFFERENT physical
// connection and you get a hang or a coin-flip instead. Pinning the pool to one
// connection makes the violation deterministic; it does not cause it.
//   dotnet run 10-efcore-pooling.cs

using Microsoft.EntityFrameworkCore;
using Npgsql;

var cs = Conn.PostgresPooled;   // Npgsql pooling ON, max pool size 1

Console.WriteLine($"EF Core -> {cs}\n");

await Scenario1_SingleStatement();
await Scenario2_HeldOpen();
await Scenario3_Transaction();
await Scenario4_XactWithoutTransaction();

Console.WriteLine("""

    The rule for EF Core, in one line:

        For request-scoped work: pg_advisory_xact_lock inside an explicit
        transaction. Never pg_advisory_lock on a POOLED connection.

    A session-scoped advisory lock is bound to the CONNECTION, and under EF
    Core you do not own the connection -- the pool does. Your DbContext is
    scoped to a request; the connection underneath it is not.

    The word "pooled" is load-bearing. Session-scoped advisory locks are the
    RIGHT tool when you want to hold something for the lifetime of a PROCESS
    rather than a request -- leader election, a singleton daemon. There the
    connection dropping is exactly the liveness signal you want: the process
    dies, the lock releases, another node takes over. No TTL, no clock.

    A background service doing leader election does exactly this, on a
    dedicated long-lived connection. What it never does is borrow one from
    the pool that is serving your HTTP requests.
    """);

// ---------------------------------------------------------------------------
static async Task Scenario1_SingleStatement()
{
    Console.WriteLine("=== 1. pg_advisory_lock via ExecuteSqlRaw ===");
    await using (var db = new Db(Conn.PostgresPooled))
    {
        await db.Database.ExecuteSqlRawAsync("select pg_advisory_lock(101)");
        Console.WriteLine("  request 1: took pg_advisory_lock(101)");
        Console.WriteLine($"             connection state right after: {db.Database.GetDbConnection().State}");
    }
    Console.WriteLine("  request 1: DbContext disposed");
    Console.WriteLine($"     server still holds it: {await Count(101)}");

    await using (var db = new Db(Conn.PostgresPooled))
    {
        // Open the connection but issue nothing: proves no reset happens on reuse.
        await db.Database.OpenConnectionAsync();
        Console.WriteLine($"  request 2: opened, no command yet -> server holds: {await Count(101)}");
        var got = await Try(db, 101);
        Console.WriteLine($"  request 2: pg_try_advisory_lock(101) -> {got.ToString().ToUpper()}");
        if (got) Console.WriteLine("             ^ two requests, same lock, both told they hold it.");
    }
    Console.WriteLine();
}

static async Task Scenario2_HeldOpen()
{
    Console.WriteLine("=== 2. same thing, connection explicitly held open ===");
    await using (var db = new Db(Conn.PostgresPooled))
    {
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("select pg_advisory_lock(102)");
        Console.WriteLine($"  request 1: took the lock, connection {db.Database.GetDbConnection().State}");
        await db.Database.CloseConnectionAsync();
    }
    Console.WriteLine($"  request 1: closed + disposed");
    Console.WriteLine($"     server still holds it: {await Count(102)}   <-- still leaked");
    Console.WriteLine("     holding the connection open changes WHEN it leaks, not WHETHER.\n");
}

static async Task Scenario3_Transaction()
{
    Console.WriteLine("=== 3. pg_advisory_xact_lock in an explicit transaction ===");
    await using (var db = new Db(Conn.PostgresPooled))
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("select pg_advisory_xact_lock(103)");
        Console.WriteLine("  request 1: took pg_advisory_xact_lock(103)");
        Console.WriteLine($"     server holds it during the tx: {await Count(103)}");
        await tx.CommitAsync();
    }
    Console.WriteLine("  request 1: committed + disposed");
    Console.WriteLine($"     server still holds it: {await Count(103)}   <-- released, correct");

    await using (var db = new Db(Conn.PostgresPooled))
        Console.WriteLine($"  request 2: pg_try_advisory_lock(103) -> {(await Try(db, 103)).ToString().ToUpper()}   (legitimately free)");
    Console.WriteLine();
}

static async Task Scenario4_XactWithoutTransaction()
{
    // The quieter bug. pg_advisory_xact_lock is the right FUNCTION, but with no
    // explicit transaction EF runs it in autocommit -- a transaction that begins
    // and ends with that one statement. The lock is taken and released in the
    // same breath, so no lock ever existed.
    Console.WriteLine("=== 4. pg_advisory_xact_lock with NO explicit transaction ===");
    await using (var db = new Db(Conn.PostgresPooled))
    {
        await db.Database.ExecuteSqlRawAsync("select pg_advisory_xact_lock(104)");
        Console.WriteLine($"  xact_lock, no explicit tx -> locks held after statement: {await Count(104)}");
    }
    Console.WriteLine(
        "\n      Zero. The right function with the wrong scope protects nothing,"
      + "\n      and nothing tells you. Both halves of the rule are load-bearing:"
      + "\n      pg_advisory_xact_lock AND an explicit BeginTransactionAsync.\n");
}

// ---------------------------------------------------------------------------
static async Task<bool> Try(Db db, long key)
{
    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"select pg_try_advisory_lock({key})";
    return (bool)(await cmd.ExecuteScalarAsync())!;
}

static async Task<long> Count(long key)
{
    // observe from OUTSIDE the pool, on a non-pooled connection
    await using var c = new NpgsqlConnection(Conn.Postgres);
    await c.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        $"select count(*) from pg_locks where locktype='advisory' and objid={key}", c);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync());
}

public class Db(string cs) : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder o) => o.UseNpgsql(cs);
}
