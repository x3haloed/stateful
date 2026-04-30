namespace Stateful;

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

    public static string ColumnType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("SQL column type cannot be empty.", nameof(value));
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != ' ' && ch != '(' && ch != ')')
            {
                throw new ArgumentException($"'{value}' is not a valid SQL column type.", nameof(value));
            }
        }

        return value;
    }

    public static string Literal(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
