#:package Aspire.Hosting.PostgreSQL@13.5.3
#:sdk Aspire.AppHost.Sdk@13.5.3

// Infrastructure for the §3 and §4 demos.
//   aspire run
//
// Host ports are PINNED, and deliberately high so they cannot collide with
// whatever else is already running on the machine. The demo scripts connect with plain
// localhost strings (see connection.cs), so you can run each one by hand at
// the moment you talk about it instead of driving them from the dashboard.

var builder = DistributedApplication.CreateBuilder(args);

var pw = builder.AddParameter("pgpass", "postgres");

var postgres = builder.AddPostgres("postgres", password: pw)
                      .WithEndpoint("tcp", e => { e.Port = 55432; e.IsProxied = false; });

var lockdb = postgres.AddDatabase("lockdb");

// Deliberately NOT builder.AddRedis(). Aspire's Redis integration defaults to
// TLS on 6379 plus a generated --requirepass, which is correct for a real app
// and pure noise for a lock demo -- it also stops you poking at the keys with
// redis-cli on stage. A plain container gives a boring, inspectable Redis.
builder.AddContainer("cache", "redis", "8-alpine")
       .WithEndpoint(port: 56379, targetPort: 6379, name: "tcp", scheme: "tcp", isProxied: false);

builder.Build().Run();
