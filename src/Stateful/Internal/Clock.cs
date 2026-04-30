namespace Stateful;

internal static class Clock
{
    public static string Now() => DateTimeOffset.UtcNow.ToString("O");
}
