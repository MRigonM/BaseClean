namespace BaseClean.Domain.Common;

/// <summary>
/// Represents an error with a code and description.
/// </summary>
public record Error(string Code, string Description)
{
    public string Code { get; } = Code;
    public string Description { get; } = Description;
}