namespace Stateful;

public sealed record Document<T>(
    string Id,
    long Version,
    T Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TableDefinition<T>(string Name);
