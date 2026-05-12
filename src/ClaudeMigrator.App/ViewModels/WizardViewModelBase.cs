using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeMigrator.App.ViewModels;

public sealed record WizardResult(bool Success, string Message);

public abstract partial class WizardViewModelBase : ViewModelBase
{
    private CancellationTokenSource? _cts;

    protected WizardViewModelBase(string title, string subtitle, IReadOnlyList<WizardStepViewModel> steps)
    {
        if (steps is null || steps.Count == 0)
        {
            throw new ArgumentException("Wizard requires at least one step.", nameof(steps));
        }

        Title = title;
        Subtitle = subtitle;
        Steps = new ReadOnlyCollection<WizardStepViewModel>(steps as IList<WizardStepViewModel> ?? new List<WizardStepViewModel>(steps));
        Steps[0].IsCurrent = true;
        LogLines = new ObservableCollection<string>();
    }

    public string Title { get; }

    public string Subtitle { get; }

    public IReadOnlyList<WizardStepViewModel> Steps { get; }

    public ObservableCollection<string> LogLines { get; }

    [ObservableProperty]
    private int currentStepIndex;

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private bool hasCompleted;

    [ObservableProperty]
    private bool hasFailed;

    [ObservableProperty]
    private string resultMessage = string.Empty;

    public WizardStepViewModel CurrentStep => Steps[CurrentStepIndex];

    public bool IsFirstStep => CurrentStepIndex == 0;

    public bool IsLastStep => CurrentStepIndex == Steps.Count - 1;

    public bool CanGoBack => !IsFirstStep && !IsRunning && !HasCompleted;

    public bool CanGoNext => !IsLastStep && !IsRunning && !HasCompleted && IsStepValid(CurrentStepIndex);

    public bool CanFinish => IsLastStep && !IsRunning && !HasCompleted && IsStepValid(CurrentStepIndex);

    public bool CanCancel => !IsRunning;

    public event EventHandler? Cancelled;

    public event EventHandler<WizardResult>? Completed;

    protected abstract bool IsStepValid(int index);

    protected abstract Task<WizardResult> ExecuteAsync(IProgress<string> log, CancellationToken cancellationToken);

    public void RaiseValidationChanged()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanFinish));
        if (CurrentStepIndex >= 0 && CurrentStepIndex < Steps.Count)
        {
            Steps[CurrentStepIndex].IsValid = IsStepValid(CurrentStepIndex);
        }
    }

    partial void OnCurrentStepIndexChanged(int oldValue, int newValue)
    {
        if (oldValue >= 0 && oldValue < Steps.Count)
        {
            Steps[oldValue].IsCurrent = false;
            if (newValue > oldValue)
            {
                Steps[oldValue].IsCompleted = true;
            }
        }

        if (newValue >= 0 && newValue < Steps.Count)
        {
            Steps[newValue].IsCurrent = true;
            Steps[newValue].IsValid = IsStepValid(newValue);
        }

        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanFinish));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanFinish));
        OnPropertyChanged(nameof(CanCancel));
    }

    partial void OnHasCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanFinish));
    }

    [RelayCommand]
    public void Next()
    {
        if (!CanGoNext)
        {
            return;
        }

        CurrentStepIndex++;
    }

    [RelayCommand]
    public void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        CurrentStepIndex--;
    }

    [RelayCommand]
    public void Cancel()
    {
        if (IsRunning)
        {
            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }
            return;
        }

        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public async Task FinishAsync()
    {
        if (!CanFinish)
        {
            return;
        }

        IsRunning = true;
        HasFailed = false;
        ResultMessage = string.Empty;
        LogLines.Clear();
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(line => LogLines.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {line}"));

        try
        {
            var result = await ExecuteAsync(progress, _cts.Token).ConfigureAwait(false);
            ResultMessage = result.Message;
            HasFailed = !result.Success;
            HasCompleted = true;
            Completed?.Invoke(this, result);
        }
        catch (OperationCanceledException)
        {
            ResultMessage = "Cancelled.";
            HasFailed = true;
            HasCompleted = true;
            Completed?.Invoke(this, new WizardResult(false, ResultMessage));
        }
        catch (Exception ex)
        {
            ResultMessage = $"Failed: {ex.Message}";
            HasFailed = true;
            HasCompleted = true;
            Completed?.Invoke(this, new WizardResult(false, ResultMessage));
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
