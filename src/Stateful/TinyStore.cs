using System.Data;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Stateful;

public sealed class TinyStore : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction? _transaction;
    private readonly JsonSerializerOptions _json;
    private bool _disposed;

    private TinyStore(SqliteConnection connection, JsonSerializerOptions? json, SqliteTransaction? transaction = null)
    {
        _connection = connection;
        _transaction = transaction;
        _json = json ?? JsonSerializerOptions.Web;
    }

    public SqliteConnection Connection => _connection;

    public static async Task<TinyStore> Open(
        string connectionString,
        Migrations? migrations = null,
        JsonSerializerOptions? json = null,
        CancellationToken ct = default)
    {
        var connection = new SqliteConnection(NormalizeConnectionString(connectionString));
        await connection.OpenAsync(ct);

        var store = new TinyStore(connection, json);

        if (migrations is not null)
        {
            await store.Migrate(migrations, ct);
        }

        return store;
    }

    public Table<T> Table<T>(string name) => new(this, name);

    public Table<T> In<T>(TableDefinition<T> table) => Table<T>(table.Name);

    public async Task<T?> Get<T>(string table, string id, CancellationToken ct = default)
        => await Table<T>(table).Get(id, ct);

    public async Task<Document<T>?> GetEnvelope<T>(string table, string id, CancellationToken ct = default)
        => await Table<T>(table).GetEnvelope(id, ct);

    public async Task Put<T>(string table, string id, T document, CancellationToken ct = default)
        => await Table<T>(table).Put(id, document, ct);

    public async Task<bool> Replace<T>(
        string table,
        string id,
        long expectedVersion,
        T document,
        CancellationToken ct = default)
        => await Table<T>(table).Replace(id, expectedVersion, document, ct);

    public async Task Delete(string table, string id, CancellationToken ct = default)
        => await Table<object>(table).Delete(id, ct);

    public async Task Patch(
        string table,
        string id,
        Action<JsonPatchBuilder> patch,
        CancellationToken ct = default)
        => await Table<object>(table).Patch(id, patch, ct);

    public async Task Patch<T>(
        string table,
        string id,
        Action<JsonPatchBuilder<T>> patch,
        CancellationToken ct = default)
        => await Table<T>(table).Patch(id, patch, ct);

    public async Task<IReadOnlyList<T>> Query<T>(
        string sql,
        object? args = null,
        CancellationToken ct = default)
    {
        await using var command = CreateCommand(sql, args);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(DeserializeBody<T>(reader));
        }

        return rows;
    }

    public async Task Transaction(Func<TinyStore, Task> work, CancellationToken ct = default)
    {
        if (_transaction is not null)
        {
            await work(this);
            return;
        }

        await using var transaction = await _connection.BeginTransactionAsync(ct);
        var txStore = new TinyStore(_connection, _json, (SqliteTransaction)transaction);

        try
        {
            await work(txStore);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task Migrate(Migrations migrations, CancellationToken ct = default)
    {
        await Execute("""
            create table if not exists schema_migrations (
                version integer primary key,
                applied_at text not null
            );
            """, ct: ct);

        var applied = new HashSet<int>();
        await using (var command = CreateCommand("select version from schema_migrations"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                applied.Add(reader.GetInt32(0));
            }
        }

        foreach (var migration in migrations.Ordered())
        {
            if (applied.Contains(migration.Version))
            {
                continue;
            }

            await Transaction(async tx =>
            {
                await tx.Execute(migration.Sql, ct: ct);
                await tx.Execute(
                    "insert into schema_migrations (version, applied_at) values ($version, $appliedAt)",
                    new { version = migration.Version, appliedAt = Clock.Now() },
                    ct);
            }, ct);
        }
    }

    public async Task<int> Execute(string sql, object? args = null, CancellationToken ct = default)
    {
        await using var command = CreateCommand(sql, args);
        return await command.ExecuteNonQueryAsync(ct);
    }

    internal SqliteCommand CreateCommand(string sql, object? args = null)
    {
        ThrowIfDisposed();

        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;

        if (args is not null)
        {
            AddParameters(command, args);
        }

        return command;
    }

    internal string Serialize<T>(T document) => JsonSerializer.Serialize(document, _json);

    internal JsonPatchBuilder CreatePatchBuilder() => new(_json);

    internal JsonPatchBuilder<T> CreatePatchBuilder<T>() => new(CreatePatchBuilder());

    internal T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, _json)
        ?? throw new InvalidOperationException($"Could not deserialize JSON as {typeof(T).Name}.");

    internal T DeserializeBody<T>(SqliteDataReader reader)
    {
        var value = reader.GetValue(0);
        if (value is string body)
        {
            return Deserialize<T>(body);
        }

        if (value is T typed)
        {
            return typed;
        }

        return (T)Convert.ChangeType(value, typeof(T));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed || _transaction is not null)
        {
            return;
        }

        await _connection.DisposeAsync();
        _disposed = true;
    }

    private static string NormalizeConnectionString(string value)
        => value.Contains('=', StringComparison.Ordinal) ? value : new SqliteConnectionStringBuilder { DataSource = value }.ToString();

    private static void AddParameters(SqliteCommand command, object args)
    {
        if (args is IReadOnlyDictionary<string, object?> dictionary)
        {
            foreach (var (name, value) in dictionary)
            {
                command.Parameters.AddWithValue(NormalizeParameterName(name), value ?? DBNull.Value);
            }

            return;
        }

        foreach (var property in args.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            command.Parameters.AddWithValue(NormalizeParameterName(property.Name), property.GetValue(args) ?? DBNull.Value);
        }
    }

    private static string NormalizeParameterName(string name)
        => name[0] is '$' or '@' or ':' ? name : "$" + name;

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TinyStore));
        }
    }
}

public sealed class Table<T>
{
    private readonly TinyStore _store;
    private readonly string _name;
    private readonly string _sqlName;

    internal Table(TinyStore store, string name)
    {
        _store = store;
        _name = name;
        _sqlName = Sql.Identifier(name);
    }

    public string Name => _name;

    internal TinyStore Store => _store;

    public async Task<T?> Get(string id, CancellationToken ct = default)
    {
        var envelope = await GetEnvelope(id, ct);
        return envelope is null ? default : envelope.Body;
    }

    public async Task<Document<T>?> GetEnvelope(string id, CancellationToken ct = default)
    {
        await using var command = _store.CreateCommand(
            $"select id, version, body, created_at, updated_at from {_sqlName} where id = $id",
            new { id });
        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new Document<T>(
            reader.GetString(0),
            reader.GetInt64(1),
            _store.Deserialize<T>(reader.GetString(2)),
            DateTimeOffset.Parse(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4)));
    }

    public async Task Put(string id, T document, CancellationToken ct = default)
    {
        var now = Clock.Now();
        await using var command = _store.CreateCommand($"""
            insert into {_sqlName} (id, version, body, created_at, updated_at)
            values ($id, 1, $body, $now, $now)
            on conflict(id) do update set
                body = excluded.body,
                version = {_sqlName}.version + 1,
                updated_at = excluded.updated_at
            """, new { id, body = _store.Serialize(document), now });

        await command.ExecuteNonQueryAsync(ct);
    }

    public Task Insert(string id, T document, CancellationToken ct = default) => InsertCore(id, document, ct);

    public async Task<bool> Replace(string id, long expectedVersion, T document, CancellationToken ct = default)
    {
        var updatedAt = Clock.Now();
        await using var command = _store.CreateCommand($"""
            update {_sqlName}
            set body = $body,
                version = version + 1,
                updated_at = $updatedAt
            where id = $id
              and version = $expectedVersion
            """, new { id, expectedVersion, body = _store.Serialize(document), updatedAt });

        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task Delete(string id, CancellationToken ct = default)
    {
        await using var command = _store.CreateCommand($"delete from {_sqlName} where id = $id", new { id });
        await command.ExecuteNonQueryAsync(ct);
    }

    public JsonPatch<T> Patch(string id) => new(this, id);

    public async Task Patch(string id, Action<JsonPatchBuilder> patch, CancellationToken ct = default)
    {
        var builder = _store.CreatePatchBuilder();
        patch(builder);
        await CommitPatch(id, builder.Build(), ct);
    }

    public async Task Patch(string id, Action<JsonPatchBuilder<T>> patch, CancellationToken ct = default)
    {
        var builder = _store.CreatePatchBuilder<T>();
        patch(builder);
        await CommitPatch(id, builder.Build(), ct);
    }

    public async Task<IReadOnlyList<T>> Query(string sql, object? args = null, CancellationToken ct = default)
    {
        var trimmed = sql.TrimStart();
        var statement = trimmed.StartsWith("select", StringComparison.OrdinalIgnoreCase)
            ? sql
            : $"select body from {_sqlName} {sql}";

        return await _store.Query<T>(statement, args, ct);
    }

    internal async Task CommitPatch(string id, IReadOnlyList<JsonPatchOperation> operations, CancellationToken ct)
    {
        if (operations.Count == 0)
        {
            return;
        }

        var parameters = new Dictionary<string, object?> { ["id"] = id, ["updatedAt"] = Clock.Now() };
        var expression = "body";
        var setValues = new List<string>();
        var removePaths = new List<string>();

        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            var pathName = $"path{i}";
            parameters[pathName] = operation.Path;

            if (operation.Kind == JsonPatchOperationKind.Set)
            {
                var valueName = $"value{i}";
                parameters[valueName] = operation.JsonValue;
                setValues.Add($"${pathName}, json(${valueName})");
            }
            else
            {
                removePaths.Add($"${pathName}");
            }
        }

        if (setValues.Count > 0)
        {
            expression = $"json_set({expression}, {string.Join(", ", setValues)})";
        }

        if (removePaths.Count > 0)
        {
            expression = $"json_remove({expression}, {string.Join(", ", removePaths)})";
        }

        await using var command = _store.CreateCommand($"""
            update {_sqlName}
            set body = {expression},
                version = version + 1,
                updated_at = $updatedAt
            where id = $id
            """, parameters);

        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertCore(string id, T document, CancellationToken ct)
    {
        var now = Clock.Now();
        await using var command = _store.CreateCommand($"""
            insert into {_sqlName} (id, version, body, created_at, updated_at)
            values ($id, 1, $body, $now, $now)
            """, new { id, body = _store.Serialize(document), now });

        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed class JsonPatch<T>
{
    private readonly Table<T> _table;
    private readonly string _id;
    private readonly JsonPatchBuilder _builder;

    internal JsonPatch(Table<T> table, string id)
    {
        _table = table;
        _id = id;
        _builder = table.Store.CreatePatchBuilder();
    }

    public JsonPatch<T> Set<TValue>(string path, TValue value)
    {
        _builder.Set(path, value);
        return this;
    }

    public JsonPatch<T> Set<TValue>(JsonPath<T, TValue> path, TValue value)
    {
        _builder.Set(path.Path, value);
        return this;
    }

    public JsonPatch<T> Remove(string path)
    {
        _builder.Remove(path);
        return this;
    }

    public JsonPatch<T> Remove<TValue>(JsonPath<T, TValue> path)
    {
        _builder.Remove(path.Path);
        return this;
    }

    public Task Commit(CancellationToken ct = default) => _table.CommitPatch(_id, _builder.Build(), ct);
}

public sealed class JsonPatchBuilder
{
    private readonly JsonSerializerOptions _json;
    private readonly List<JsonPatchOperation> _operations = [];

    internal JsonPatchBuilder(JsonSerializerOptions json)
    {
        _json = json;
    }

    public JsonPatchBuilder Set<T>(string path, T value)
    {
        _operations.Add(JsonPatchOperation.Set(path, JsonSerializer.Serialize(value, _json)));
        return this;
    }

    public JsonPatchBuilder Remove(string path)
    {
        _operations.Add(JsonPatchOperation.Remove(path));
        return this;
    }

    internal IReadOnlyList<JsonPatchOperation> Build() => _operations;
}

public sealed class JsonPatchBuilder<TDocument>
{
    private readonly JsonPatchBuilder _inner;

    internal JsonPatchBuilder(JsonPatchBuilder inner)
    {
        _inner = inner;
    }

    public JsonPatchBuilder<TDocument> Set<TValue>(JsonPath<TDocument, TValue> path, TValue value)
    {
        _inner.Set(path.Path, value);
        return this;
    }

    public JsonPatchBuilder<TDocument> Remove<TValue>(JsonPath<TDocument, TValue> path)
    {
        _inner.Remove(path.Path);
        return this;
    }

    internal IReadOnlyList<JsonPatchOperation> Build() => _inner.Build();
}

public readonly record struct JsonPath<TDocument, TValue>(string Path)
{
    public static JsonPath<TDocument, TValue> Create(string path) => new(path);

    public override string ToString() => Path;
}

public readonly record struct JsonObjectPath<TDocument>(string Path)
{
    public JsonPath<TDocument, TValue> Field<TValue>(string name)
        => new(JsonPath.Join(Path, name));

    public JsonObjectPath<TDocument> Object(string name)
        => new(JsonPath.Join(Path, name));

    public JsonArrayPath<TDocument, TItem> Array<TItem>(string name)
        => new(JsonPath.Join(Path, name));

    public static JsonObjectPath<TDocument> Root => new("$");

    public override string ToString() => Path;
}

public readonly record struct JsonArrayPath<TDocument, TItem>(string Path)
{
    public JsonObjectPath<TDocument> At(int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "JSON array index cannot be negative.");
        }

        return new JsonObjectPath<TDocument>($"{Path}[{index}]");
    }

    public JsonPath<TDocument, TItem> AtValue(int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "JSON array index cannot be negative.");
        }

        return new JsonPath<TDocument, TItem>($"{Path}[{index}]");
    }

    public override string ToString() => Path;
}

public sealed record Document<T>(
    string Id,
    long Version,
    T Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TableDefinition<T>(string Name);

public static class JsonPath
{
    public static JsonObjectPath<TDocument> For<TDocument>() => JsonObjectPath<TDocument>.Root;

    public static JsonPath<TDocument, TValue> Create<TDocument, TValue>(string path)
        => new(path);

    internal static string Join(string parent, string member)
    {
        if (string.IsNullOrWhiteSpace(member))
        {
            throw new ArgumentException("JSON path member cannot be empty.", nameof(member));
        }

        if (member.Any(ch => ch is '.' or '[' or ']' or '"' or '\''))
        {
            throw new ArgumentException($"'{member}' is not a simple JSON path member.", nameof(member));
        }

        return parent == "$" ? "$." + member : parent + "." + member;
    }
}

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

internal enum JsonPatchOperationKind
{
    Set,
    Remove
}

internal sealed record JsonPatchOperation(JsonPatchOperationKind Kind, string Path, string? JsonValue)
{
    public static JsonPatchOperation Set(string path, string jsonValue) => new(JsonPatchOperationKind.Set, path, jsonValue);

    public static JsonPatchOperation Remove(string path) => new(JsonPatchOperationKind.Remove, path, null);
}

internal static class Sql
{
    public static string Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("SQL identifier cannot be empty.", nameof(value));
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_')
            {
                throw new ArgumentException($"'{value}' is not a valid SQL identifier.", nameof(value));
            }
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

internal static class Clock
{
    public static string Now() => DateTimeOffset.UtcNow.ToString("O");
}
