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

    public async Task<bool> Patch(
        string table,
        string id,
        Action<JsonPatchBuilder> patch,
        CancellationToken ct = default)
        => await Table<object>(table).Patch(id, patch, ct);

    public async Task<bool> Patch<T>(
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
