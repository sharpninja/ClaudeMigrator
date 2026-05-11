namespace ClaudeMigrator.Core.Models;

public enum StepStatus
{
    Pending,
    Queued,
    Running,
    Waiting,
    Partial,
    Done,
    Complete,
    Failed,
    Cancelled,
}

public sealed record StepDefinition(
    string StepId,
    string Title,
    string Description);

public sealed record StepState(
    string StepId,
    string Title,
    string Description,
    StepStatus Status = StepStatus.Pending,
    int Progress = 0,
    string Detail = "");

public sealed record ManualAction(
    string StepId,
    string Label,
    string Message,
    string Kind = "continue");
