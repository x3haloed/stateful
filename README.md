# Stateful

Stateful is a tiny SQLite document store for C# applications. It stores typed objects as JSON documents, lets you patch durable state directly, and gets out of the way when you need SQL.

Normalize later. Index what you query. Keep state simple.

```csharp
var migrations = new Migrations().Add(1, """
    create table customers (
        id text primary key,
        version integer not null default 1,
        body text not null check (json_valid(body)),
        created_at text not null,
        updated_at text not null,
        name text generated always as (json_extract(body, '$.name')) stored
    );

    create index ix_customers_name on customers(name);
    """);

await using var db = await TinyStore.Open("app.db", migrations);

var customers = db.Table<Customer>("customers");

await customers.Put("customer/123", new Customer
{
    Id = "customer/123",
    Name = "Acme"
});

await customers.Patch("customer/123")
    .Set("$.name", "Acme Corp")
    .Commit();

var customer = await customers.Get("customer/123");

var matches = await customers.Query("""
    where name like $name
    order by updated_at desc
    """, new { name = "%Acme%" });
```

The database is still SQLite. Query with SQL when SQL earns its keep.

```csharp
var matches = await db.Query<Customer>("""
    select body
    from customers
    where name like $name
    order by updated_at desc
    limit 50
    """, new { name = "%acme%" });
```

Optimistic concurrency is built into `Replace`:

```csharp
var envelope = await customers.GetEnvelope("customer/123");

var replaced = await customers.Replace(
    "customer/123",
    expectedVersion: envelope!.Version,
    document: envelope.Body with { Name = "Acme Inc" });
```
