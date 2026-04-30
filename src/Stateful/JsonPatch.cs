using System.Text.Json;

namespace Stateful;

public sealed class JsonPatch<T>
{
    private readonly Table<T> _table;
    private readonly string _id;
    private readonly JsonPatchBuilder _builder;
    private long? _expectedVersion;

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

    public JsonPatch<T> IfVersion(long expectedVersion)
    {
        _expectedVersion = expectedVersion;
        return this;
    }

    public Task<bool> Commit(CancellationToken ct = default)
        => _table.CommitPatch(_id, _builder.Build(), _expectedVersion, ct);
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
