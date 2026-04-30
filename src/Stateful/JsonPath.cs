namespace Stateful;

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
