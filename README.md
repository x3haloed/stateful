# Stateful

Stateful is a tiny SQLite document store for C# applications. It stores typed objects as JSON documents, lets you patch durable state directly, and gets out of the way when you need SQL.

Normalize later. Index what you query. Keep state simple.

```csharp
var migrations = new Migrations().Add(1, Schema
    .DocumentTable(Tables.Customers)
    .Generated("name", CustomerPaths.Name)
    .Index("ix_customers_name", "name")
    .ToString());

await using var db = await TinyStore.Open("app.db", migrations);

var customers = db.In(Tables.Customers);

await customers.Put("customer/123", new Customer
{
    Id = "customer/123",
    Name = "Acme"
});

await customers.Patch("customer/123")
    .Set(CustomerPaths.Name, "Acme Corp")
    .Commit();

var customer = await customers.Get("customer/123");

var matches = await customers.Query("""
    where name like $name
    order by updated_at desc
    """, new { name = "%Acme%" });
```

Typed paths are just symbols over JSON paths. They give the compiler enough shape to reject wrong document/value combinations without turning Stateful into an ORM:

```csharp
public static class Tables
{
    public static readonly TableDefinition<Customer> Customers = new("customers");
}

public static class CustomerPaths
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

await customers.Patch("customer/123")
    .Set(CustomerPaths.Name, "Acme Corp")
    .Set(CustomerPaths.Billing.Terms, "Net30")
    .Commit();
```

Path catalogs can also be generated:

```csharp
[GenerateJsonPaths]
public sealed record Customer(
    string Id,
    string Name,
    Billing Billing);

public sealed record Billing(string Terms);

await customers.Patch("customer/123")
    .Set(CustomerPaths.Name, "Acme Corp")
    .Set(CustomerPaths.Billing.Terms, "Net30")
    .Commit();
```

The string-path API remains available when you need the escape hatch:

```csharp
await customers.Patch("customer/123")
    .Set("$.experimental.newShape", new { enabled = true })
    .Commit();
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

Patches can use the same optimistic concurrency guard:

```csharp
var patched = await customers.Patch("customer/123")
    .IfVersion(envelope.Version)
    .Set(CustomerPaths.Name, "Acme Corp")
    .Commit();
```

Migrations can use typed paths too. The generated column is still SQLite, but the JSON path is not duplicated as a string:

```csharp
migrations.Add(2, Schema
    .DocumentTable(Tables.Customers)
    .Generated("primary_email", CustomerPaths.PrimaryEmail)
    .Index("ix_customers_primary_email", "primary_email")
    .ToString());
```

The optional analyzer reports an informational diagnostic when patch code uses raw JSON path strings where a typed symbol would give the compiler more to work with.
