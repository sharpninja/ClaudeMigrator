using ClaudeMigrator.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeMigrator.App.ViewModels;

public sealed partial class StepViewModel : ObservableObject
{
    [ObservableProperty]
    private string stepId = string.Empty;

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string description = string.Empty;

    [ObservableProperty]
    private StepStatus status = StepStatus.Pending;

    [ObservableProperty]
    private int progress;

    [ObservableProperty]
    private string detail = string.Empty;

    public void Update(StepState state)
    {
        StepId = state.StepId;
        Title = state.Title;
        Description = state.Description;
        Status = state.Status;
        Progress = state.Progress;
        Detail = state.Detail;
    }
}
