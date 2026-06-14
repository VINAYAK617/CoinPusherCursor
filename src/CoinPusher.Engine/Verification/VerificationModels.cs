namespace CoinPusher.Engine;

public enum VerificationSeverity
{
    Error
}

public sealed record VerificationIssue(VerificationSeverity Severity, string Code, string Message);

public sealed record VerificationReport(bool IsValid, IReadOnlyList<VerificationIssue> Issues, SimulationResult? SimulationResult);
