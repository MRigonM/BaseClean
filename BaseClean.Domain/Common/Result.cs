namespace BaseClean.Domain.Common;

public class Result<T>
{
     private readonly List<Error> _errors = new();

    public bool IsSuccess { get; }
    public T? Value { get; }
    public IReadOnlyList<Error> Errors => _errors.AsReadOnly();

    private Result(bool isSuccess, T? value, IReadOnlyList<Error> errors)
    {
        IsSuccess = isSuccess;
        Value = value;

        if (errors.Any())
            _errors.AddRange(errors);
    }

    /// <summary>
    /// Creates a successful result with the specified value.
    /// </summary>
    public static Result<T> Success(T? value = default) =>
        new Result<T>(true, value, new List<Error>());

    /// <summary>
    /// Creates a failed result with a general error message.
    /// </summary>
    public static Result<T> Failure(string error) =>
        new Result<T>(false, default, new List<Error> { new Error("General.Error", error) });

    /// <summary>
    /// Creates a failed result with one or more specific errors.
    /// </summary>
    public static Result<T> Failure(params Error[] errors) =>
        new Result<T>(false, default, errors);
}

/// <summary>
/// Represents the result of a void operation (no return value).
/// </summary>
public class Result
{
    private readonly List<Error> _errors = new();

    public bool IsSuccess { get; }
    public IReadOnlyList<Error> Errors => _errors.AsReadOnly();

    private Result(bool isSuccess, IReadOnlyList<Error> errors)
    {
        IsSuccess = isSuccess;
        if (errors.Any())
            _errors.AddRange(errors);
    }
}
