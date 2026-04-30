namespace Stateful;

public static class Schema
{
    public static DocumentTableSchema<T> DocumentTable<T>(TableDefinition<T> table)
        => new(table.Name);

    public static DocumentTableSchema<T> DocumentTable<T>(string table)
        => new(table);
}

public sealed class DocumentTableSchema<T>
{
    private readonly string _table;
    private readonly List<string> _generatedColumns = [];
    private readonly List<string> _indexes = [];

    internal DocumentTableSchema(string table)
    {
        _table = Sql.Identifier(table);
    }

    public DocumentTableSchema<T> Generated<TValue>(string column, JsonPath<T, TValue> path, string type = "text")
    {
        _generatedColumns.Add($"{Sql.Identifier(column)} {Sql.ColumnType(type)} generated always as (json_extract(body, {Sql.Literal(path.Path)})) stored");
        return this;
    }

    public DocumentTableSchema<T> Index(string name, params string[] columns)
    {
        if (columns.Length == 0)
        {
            throw new ArgumentException("Index must include at least one column.", nameof(columns));
        }

        _indexes.Add($"create index if not exists {Sql.Identifier(name)} on {_table}({string.Join(", ", columns.Select(Sql.Identifier))});");
        return this;
    }

    public override string ToString()
    {
        var columns = new List<string>
        {
            "id text primary key",
            "version integer not null default 1",
            "body text not null check (json_valid(body))",
            "created_at text not null",
            "updated_at text not null"
        };

        columns.AddRange(_generatedColumns);

        return $"""
            create table if not exists {_table} (
                {string.Join(",\n    ", columns)}
            );

            {string.Join("\n", _indexes)}
            """;
    }
}
