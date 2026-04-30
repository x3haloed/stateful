using Stateful;

namespace Stateful.Tests;

public sealed class TinyStoreTests
{
    [Fact]
    public async Task PutGetAndPatchRoundTripJsonDocuments()
    {
        await using var db = await OpenStore();
        var customers = db.Table<Customer>("customers");

        await customers.Put("customer/123", new Customer("customer/123", "Acme", "old@example.com", null));
        await customers.Patch("customer/123")
            .Set("$.name", "Acme Corp")
            .Set("$.primaryEmail", "billing@acme.test")
            .Set("$.billing", new Billing("Net30"))
            .Commit();

        var customer = await customers.Get("customer/123");
        var envelope = await customers.GetEnvelope("customer/123");

        Assert.Equal("Acme Corp", customer!.Name);
        Assert.Equal("billing@acme.test", customer.PrimaryEmail);
        Assert.Equal("Net30", customer.Billing!.Terms);
        Assert.Equal(2, envelope!.Version);
    }

    [Fact]
    public async Task ReplaceUsesExpectedVersion()
    {
        await using var db = await OpenStore();
        var customers = db.Table<Customer>("customers");

        await customers.Put("customer/123", new Customer("customer/123", "Acme", null, null));
        var envelope = await customers.GetEnvelope("customer/123");

        var replaced = await customers.Replace(
            "customer/123",
            envelope!.Version,
            envelope.Body with { Name = "Acme Inc" });

        var staleReplace = await customers.Replace(
            "customer/123",
            envelope.Version,
            envelope.Body with { Name = "Stale" });

        var current = await customers.GetEnvelope("customer/123");

        Assert.True(replaced);
        Assert.False(staleReplace);
        Assert.Equal("Acme Inc", current!.Body.Name);
        Assert.Equal(2, current.Version);
    }

    [Fact]
    public async Task QueryLetsSqlStaySql()
    {
        await using var db = await OpenStore();
        var customers = db.Table<Customer>("customers");

        await customers.Put("customer/1", new Customer("customer/1", "Acme", null, null));
        await customers.Put("customer/2", new Customer("customer/2", "Other", null, null));

        var matches = await customers.Query("""
            where name like $name
            order by updated_at desc
            """, new { name = "%Acme%" });

        Assert.Single(matches);
        Assert.Equal("Acme", matches[0].Name);
    }

    [Fact]
    public async Task MigrationsRunOnce()
    {
        var migrations = new Migrations()
            .Add(1, "create table things (id text primary key);")
            .Add(2, "alter table things add column body text;");

        await using var db = await TinyStore.Open("Data Source=:memory:", migrations);
        await db.Migrate(migrations);

        var applied = await db.Query<int>("select version from schema_migrations order by version");

        Assert.Equal([1, 2], applied);
    }

    [Fact]
    public async Task TransactionCommitsMultipleTablesTogether()
    {
        await using var db = await OpenStore();

        await db.Transaction(async tx =>
        {
            await tx.Table<Customer>("customers").Put("customer/123", new Customer("customer/123", "Acme", null, null));
            await tx.Table<Invoice>("invoices").Put("invoice/123", new Invoice("invoice/123", "customer/123", "Open"));
        });

        Assert.NotNull(await db.Table<Customer>("customers").Get("customer/123"));
        Assert.NotNull(await db.Table<Invoice>("invoices").Get("invoice/123"));
    }

    [Fact]
    public async Task RejectsUnsafeTableNames()
    {
        await using var db = await OpenStore();

        Assert.Throws<ArgumentException>(() => db.Table<Customer>("customers; drop table customers"));
    }

    private static Task<TinyStore> OpenStore()
    {
        var migrations = new Migrations().Add(1, """
            create table customers (
                id text primary key,
                version integer not null default 1,
                body text not null check (json_valid(body)),
                created_at text not null,
                updated_at text not null,
                name text generated always as (json_extract(body, '$.name')) stored,
                primary_email text generated always as (json_extract(body, '$.primaryEmail')) stored
            );

            create index ix_customers_name on customers(name);
            create index ix_customers_primary_email on customers(primary_email);

            create table invoices (
                id text primary key,
                version integer not null default 1,
                body text not null check (json_valid(body)),
                created_at text not null,
                updated_at text not null,
                status text generated always as (json_extract(body, '$.status')) stored
            );
            """);

        return TinyStore.Open("Data Source=:memory:", migrations);
    }

    private sealed record Customer(string Id, string Name, string? PrimaryEmail, Billing? Billing);

    private sealed record Billing(string Terms);

    private sealed record Invoice(string Id, string CustomerId, string Status);
}
