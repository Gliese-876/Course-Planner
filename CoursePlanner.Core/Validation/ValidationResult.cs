using System.Text.Json.Serialization;

namespace CoursePlanner.Core;

public sealed class ValidationIssue
{
    public string Code { get; set; } = "";
    public List<string> Parameters { get; set; } = new();
}

public sealed class ValidationResult
{
    public List<ValidationIssue> Errors { get; } = new();
    public List<ValidationIssue> Warnings { get; } = new();

    [JsonIgnore]
    public bool IsValid => Errors.Count == 0;

    [JsonIgnore]
    public bool RequiresForce => IsValid && Warnings.Any(x =>
        x.Code.Contains("OutOfRange", StringComparison.Ordinal) ||
        x.Code.Contains("Bounded", StringComparison.Ordinal));

    public void Error(string code, params string[] parameters) =>
        Errors.Add(new ValidationIssue { Code = code, Parameters = parameters.ToList() });

    public void Warning(string code, params string[] parameters) =>
        Warnings.Add(new ValidationIssue { Code = code, Parameters = parameters.ToList() });
}
