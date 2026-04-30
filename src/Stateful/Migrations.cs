namespace Stateful;

public sealed class Migrations
{
    private readonly SortedDictionary<int, string> _migrations = [];

    public Migrations Add(int version, string sql)
    {
        if (!_migrations.TryAdd(version, sql))
        {
            throw new InvalidOperationException($"Migration {version} already exists.");
        }

        return this;
    }

    internal IEnumerable<MigrationStep> Ordered()
        => _migrations.Select(pair => new MigrationStep(pair.Key, pair.Value));
}

internal sealed record MigrationStep(int Version, string Sql);
