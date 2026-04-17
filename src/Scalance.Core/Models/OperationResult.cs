namespace Scalance.Core.Models;

public sealed record OperationResult(
    bool Success,
    string? Message = null,
    Exception? Error = null)
{
    public static OperationResult Ok(string? message = null) => new(true, message);
    public static OperationResult Fail(string message, Exception? error = null) => new(false, message, error);
}

public sealed record OperationResult<T>(
    bool Success,
    T? Value,
    string? Message = null,
    Exception? Error = null)
{
    public static OperationResult<T> Ok(T value, string? message = null) => new(true, value, message);
    public static OperationResult<T> Fail(string message, Exception? error = null) => new(false, default, message, error);
}
