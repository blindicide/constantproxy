namespace ConstantProxy.Core.Validation;

public enum IssueSeverity
{
    Warning,
    Error,
}

/// <summary>A validation finding. <see cref="Code"/> is a stable key suitable for localization.</summary>
public sealed record ValidationIssue(IssueSeverity Severity, string Field, string Code, string Message);

public sealed class ValidationResult
{
    private readonly List<ValidationIssue> issues = new();

    public IReadOnlyList<ValidationIssue> Issues => issues;

    public IEnumerable<ValidationIssue> Errors => issues.Where(i => i.Severity == IssueSeverity.Error);

    public IEnumerable<ValidationIssue> Warnings => issues.Where(i => i.Severity == IssueSeverity.Warning);

    public bool IsValid => !Errors.Any();

    public void Error(string field, string code, string message) =>
        issues.Add(new ValidationIssue(IssueSeverity.Error, field, code, message));

    public void Warning(string field, string code, string message) =>
        issues.Add(new ValidationIssue(IssueSeverity.Warning, field, code, message));

    public void AddRange(IEnumerable<ValidationIssue> other) => issues.AddRange(other);
}
