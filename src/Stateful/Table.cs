namespace Stateful;

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

    public async Task<bool> Patch(string id, Action<JsonPatchBuilder> patch, CancellationToken ct = default)
    {
        var builder = _store.CreatePatchBuilder();
        patch(builder);
        return await CommitPatch(id, builder.Build(), expectedVersion: null, ct);
    }

    public async Task<bool> Patch(string id, Action<JsonPatchBuilder<T>> patch, CancellationToken ct = default)
    {
        var builder = _store.CreatePatchBuilder<T>();
        patch(builder);
        return await CommitPatch(id, builder.Build(), expectedVersion: null, ct);
    }

    public async Task<IReadOnlyList<T>> Query(string sql, object? args = null, CancellationToken ct = default)
    {
        var trimmed = sql.TrimStart();
        var statement = trimmed.StartsWith("select", StringComparison.OrdinalIgnoreCase)
            ? sql
            : $"select body from {_sqlName} {sql}";

        return await _store.Query<T>(statement, args, ct);
    }

    internal async Task<bool> CommitPatch(
        string id,
        IReadOnlyList<JsonPatchOperation> operations,
        long? expectedVersion,
        CancellationToken ct)
    {
        if (operations.Count == 0)
        {
            return true;
        }

        var parameters = new Dictionary<string, object?> { ["id"] = id, ["updatedAt"] = Clock.Now() };
        var versionPredicate = "";

        if (expectedVersion is not null)
        {
            parameters["expectedVersion"] = expectedVersion.Value;
            versionPredicate = " and version = $expectedVersion";
        }

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
            {versionPredicate}
            """, parameters);

        return await command.ExecuteNonQueryAsync(ct) == 1;
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
