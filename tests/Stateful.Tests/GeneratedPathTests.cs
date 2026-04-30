using System.Text.Json.Serialization;
using Stateful;

namespace Stateful.Tests;

public sealed class GeneratedPathTests
{
    [Fact]
    public async Task GeneratedPathsCanPatchDocumentFields()
    {
        var migrations = new Migrations().Add(1, Schema
            .DocumentTable(Tables.GeneratedCustomers)
            .Generated("display_name", GeneratedCustomerPaths.DisplayName)
            .Index("ix_generated_customers_display_name", "display_name")
            .ToString());

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

public static class Tables
{
    public static readonly TableDefinition<GeneratedCustomer> GeneratedCustomers = new("generated_customers");
}

[GenerateJsonPaths]
public sealed record GeneratedCustomer(
    string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    GeneratedBilling Billing);

public sealed record GeneratedBilling(string Terms);
