using System.Text.Json.Serialization;
using Stateful;

namespace Stateful.Tests;

public sealed class GeneratedPathTests
{
    [Fact]
    public async Task GeneratedPathsCanPatchDocumentFields()
    {
        var migrations = new Migrations().Add(1, """
            create table generated_customers (
                id text primary key,
                version integer not null default 1,
                body text not null check (json_valid(body)),
                created_at text not null,
                updated_at text not null,
                display_name text generated always as (json_extract(body, '$.display_name')) stored
            );
            """);

        await using var db = await TinyStore.Open("Data Source=:memory:", migrations);
        var customers = db.Table<GeneratedCustomer>("generated_customers");

        await customers.Put("customer/123", new GeneratedCustomer("customer/123", "Acme", new GeneratedBilling("Net30")));

        var patched = await customers.Patch("customer/123")
            .Set(GeneratedCustomerPaths.DisplayName, "Acme Corp")
            .Set(GeneratedCustomerPaths.Billing.Terms, "Net15")
            .Commit();

        var customer = await customers.Get("customer/123");

        Assert.True(patched);
        Assert.Equal("Acme Corp", customer!.DisplayName);
        Assert.Equal("Net15", customer.Billing.Terms);
    }
}

[GenerateJsonPaths]
public sealed record GeneratedCustomer(
    string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    GeneratedBilling Billing);

public sealed record GeneratedBilling(string Terms);
