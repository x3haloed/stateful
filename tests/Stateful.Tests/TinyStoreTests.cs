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

        await customers.Put("customer/123", new Customer("customer/123", "Acme", null, new Billing("Net30")));
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
    public async Task TypedPathsPatchDocumentsWithoutStringPathsAtCallSite()
    {
        await using var db = await OpenStore();
        var customers = db.In(Tables.Customers);

        await customers.Put("customer/123", new Customer("customer/123", "Acme", null, new Billing("Net30")));
        await customers.Patch("customer/123")
            .Set(CustomerPaths.Name, "Acme Corp")
            .Set(CustomerPaths.PrimaryEmail, "ops@acme.test")
            .Set(CustomerPaths.Billing.Terms, "Net15")
            .Commit();

        var customer = await customers.Get("customer/123");

        Assert.Equal("Acme Corp", customer!.Name);
        Assert.Equal("ops@acme.test", customer.PrimaryEmail);
        Assert.Equal("Net15", customer.Billing!.Terms);
    }

    [Fact]
    public async Task TypedPatchCallbackUsesDocumentSpecificPaths()
    {
        await using var db = await OpenStore();

        await db.Put("customers", "customer/123", new Customer("customer/123", "Acme", "ops@acme.test", null));
        await db.Patch<Customer>("customers", "customer/123", patch => patch
            .Set(CustomerPaths.Name, "Acme Ltd")
            .Remove(CustomerPaths.PrimaryEmail));

        var customer = await db.Get<Customer>("customers", "customer/123");

        Assert.Equal("Acme Ltd", customer!.Name);
        Assert.Null(customer.PrimaryEmail);
    }

    [Fact]
    public async Task PatchCanUseExpectedVersion()
    {
        await using var db = await OpenStore();
        var customers = db.In(Tables.Customers);

        await customers.Put("customer/123", new Customer("customer/123", "Acme", null, null));
        var envelope = await customers.GetEnvelope("customer/123");

        var patched = await customers.Patch("customer/123")
            .IfVersion(envelope!.Version)
            .Set(CustomerPaths.Name, "Acme Corp")
            .Commit();

        var stalePatch = await customers.Patch("customer/123")
            .IfVersion(envelope.Version)
            .Set(CustomerPaths.Name, "Stale")
            .Commit();

        var current = await customers.GetEnvelope("customer/123");

        Assert.True(patched);
        Assert.False(stalePatch);
        Assert.Equal("Acme Corp", current!.Body.Name);
        Assert.Equal(2, current.Version);
    }

    [Fact]
    public async Task CallbackPatchReturnsWhetherItModifiedARow()
    {
        await using var db = await OpenStore();

        var patched = await db.Patch<Customer>("customers", "missing", patch => patch.Set(CustomerPaths.Name, "Nope"));

        Assert.False(patched);
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

    private static class Tables
    {
        public static readonly TableDefinition<Customer> Customers = new("customers");
    }

    private static class CustomerPaths
    {
        private static readonly JsonObjectPath<Customer> Root = JsonPath.For<Customer>();

        public static readonly JsonPath<Customer, string> Name = Root.Field<string>("name");
        public static readonly JsonPath<Customer, string?> PrimaryEmail = Root.Field<string?>("primaryEmail");

        public static class Billing
        {
            private static readonly JsonObjectPath<Customer> Path = Root.Object("billing");

            public static readonly JsonPath<Customer, string> Terms = Path.Field<string>("terms");
        }
    }
}
