namespace Stateful;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class GenerateJsonPathsAttribute : Attribute
{
    public GenerateJsonPathsAttribute()
    {
    }

    public GenerateJsonPathsAttribute(string className)
    {
        ClassName = className;
    }

    public string? ClassName { get; }
}
