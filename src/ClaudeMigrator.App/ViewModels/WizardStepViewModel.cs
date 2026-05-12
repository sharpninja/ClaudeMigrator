using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeMigrator.App.ViewModels;

public sealed partial class WizardStepViewModel : ViewModelBase
{
    public WizardStepViewModel(string stepId, string title, string description)
    {
        StepId = stepId;
        Title = title;
        Description = description;
    }

    public string StepId { get; }

    public string Title { get; }

    public string Description { get; }

    [ObservableProperty]
    private bool isCurrent;

    [ObservableProperty]
    private bool isCompleted;

    [ObservableProperty]
    private bool isValid = true;

    [ObservableProperty]
    private string? validationMessage;
}
