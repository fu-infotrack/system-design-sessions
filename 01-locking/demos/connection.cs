// Shared connection strings for the demo scripts.
// Pulled in with `#:include connection.cs` — no top-level statements here.
//
// These match the pinned host ports in apphost.cs. Override with env vars
// if you are running your own containers.

public static class Conn
{
    const string PgBase = "Host=localhost;Port=55432;Username=postgres;Password=postgres;Database=lockdb";

    /// Direct to Postgres, client-side pooling OFF. Close() really closes.
    public static string Postgres =>
        Environment.GetEnvironmentVariable("PG_DIRECT") ?? PgBase + ";Pooling=false";

    /// Direct to Postgres, Npgsql's own pool ON — Close() returns to the pool.
    public static string PostgresPooled =>
        (Environment.GetEnvironmentVariable("PG_DIRECT") ?? PgBase)
        + ";Pooling=true;Minimum Pool Size=1;Maximum Pool Size=1";

    public static string PgBouncer =>
        Environment.GetEnvironmentVariable("PG_BOUNCER")
        ?? "Host=localhost;Port=56432;Username=postgres;Password=postgres;Database=lockdb;"
         + "Pooling=false";   // we want raw connections; PgBouncer is the pool

    public static string Redis =>
        Environment.GetEnvironmentVariable("REDIS")
        ?? "localhost:56379";
}
