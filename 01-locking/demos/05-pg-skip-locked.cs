#!/usr/bin/env -S dotnet --
#:package Npgsql@9.*
#:property PublishAot=false
#:include connection.cs

// §3.2 — FOR UPDATE SKIP LOCKED is a work queue.
//   dotnet run 05-pg-skip-locked.cs             SKIP LOCKED
//   dotnet run 05-pg-skip-locked.cs -- --block  plain FOR UPDATE, for contrast

using System.Diagnostics;
using Npgsql;

var block = args.Contains("--block");
const int Workers = 4;
const int Jobs = 12;

await using (var setup = new NpgsqlConnection(Conn.Postgres))
{
    await setup.OpenAsync();
    await Run(setup, "drop table if exists jobs");
    await Run(setup, "create table jobs (id int primary key, taken_by int)");
    await Run(setup, $"insert into jobs select g, null from generate_series(1,{Jobs}) g");
}

Console.WriteLine($"{Jobs} jobs, {Workers} workers, {(block ? "FOR UPDATE" : "FOR UPDATE SKIP LOCKED")}\n");

var sw = Stopwatch.StartNew();
var claims = new System.Collections.Concurrent.ConcurrentBag<(int Worker, int Job)>();

await Task.WhenAll(Enumerable.Range(1, Workers).Select(async w =>
{
    await using var c = new NpgsqlConnection(Conn.Postgres);
    await c.OpenAsync();

    while (true)
    {
        await using var tx = await c.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand($"""
            select id from jobs where taken_by is null
            order by id
            for update {(block ? "" : "skip locked")}
            limit 1
            """, c, tx);
        cmd.CommandTimeout = 10;

        var id = await cmd.ExecuteScalarAsync();
        if (id is null) { await tx.CommitAsync(); break; }

        await Task.Delay(120);                       // "doing the job"
        await Run(c, $"update jobs set taken_by = {w} where id = {(int)id}", tx);
        await tx.CommitAsync();
        claims.Add((w, (int)id));
    }
}));
sw.Stop();

foreach (var g in claims.GroupBy(x => x.Worker).OrderBy(g => g.Key))
    Console.WriteLine($"  worker {g.Key}: {g.Count(),2} jobs  {string.Join(" ", g.Select(x => x.Job).Order())}");

var dupes = claims.GroupBy(x => x.Job).Where(g => g.Count() > 1).ToList();
Console.WriteLine($"\n  total claimed:  {claims.Count} of {Jobs}");
Console.WriteLine($"  duplicates:     {dupes.Count}");
Console.WriteLine($"  wall clock:     {sw.ElapsedMilliseconds} ms");

Console.WriteLine(block
    ? """

      Plain FOR UPDATE: correct, but the workers QUEUE. Each one blocks on
      the row the previous worker locked, so they take turns and you get
      roughly no parallelism at all. Compare the wall clock.
      """
    : """

      SKIP LOCKED: every worker takes a DIFFERENT row instead of waiting for
      the one in front. No duplicates, no coordinator, no lock service, no
      broker -- and the work actually runs in parallel.

      Run with --block to see the same thing without SKIP LOCKED.

      This is the Competing Consumer pattern in one keyword.
      """);

static async Task Run(NpgsqlConnection c, string sql, NpgsqlTransaction? tx = null)
{
    await using var cmd = new NpgsqlCommand(sql, c, tx);
    await cmd.ExecuteNonQueryAsync();
}
