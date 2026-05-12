using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClaudeMigrator.App.ViewModels;

namespace ClaudeMigrator.Tests;

public sealed class WizardViewModelBaseTests
{
    [Fact]
    public void StartsAtFirstStep()
    {
        var wizard = new TestWizard();

        Assert.Equal(0, wizard.CurrentStepIndex);
        Assert.True(wizard.IsFirstStep);
        Assert.False(wizard.IsLastStep);
        Assert.True(wizard.CurrentStep.IsCurrent);
        Assert.False(wizard.Steps[1].IsCurrent);
        Assert.True(wizard.CanGoNext);
        Assert.False(wizard.CanGoBack);
        Assert.False(wizard.CanFinish);
    }

    [Fact]
    public void NextAdvancesAndMarksPreviousCompleted()
    {
        var wizard = new TestWizard();

        wizard.Next();

        Assert.Equal(1, wizard.CurrentStepIndex);
        Assert.True(wizard.Steps[0].IsCompleted);
        Assert.True(wizard.Steps[1].IsCurrent);
        Assert.True(wizard.CanGoBack);
    }

    [Fact]
    public void NextBlockedWhenStepInvalid()
    {
        var wizard = new TestWizard();
        wizard.Next();

        Assert.False(wizard.CanGoNext);
        wizard.Next();
        Assert.Equal(1, wizard.CurrentStepIndex);

        wizard.Step1Value = "anything";
        Assert.True(wizard.CanGoNext);
        wizard.Next();
        Assert.Equal(2, wizard.CurrentStepIndex);
    }

    [Fact]
    public void CanFinishOnlyOnLastStep()
    {
        var wizard = new TestWizard();
        wizard.Step1Value = "ok";

        Assert.False(wizard.CanFinish);
        wizard.Next();
        Assert.False(wizard.CanFinish);
        wizard.Next();

        Assert.True(wizard.IsLastStep);
        Assert.True(wizard.CanFinish);
    }

    [Fact]
    public async Task FinishExecutesAndFiresCompleted()
    {
        var wizard = new TestWizard();
        WizardResult? captured = null;
        wizard.Completed += (_, result) => captured = result;
        wizard.Step1Value = "ok";
        wizard.Next();
        wizard.Next();

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.True(wizard.HasCompleted);
        Assert.False(wizard.HasFailed);
        Assert.True(wizard.ExecutedCount == 1);
        Assert.NotNull(captured);
        Assert.True(captured!.Success);
    }

    [Fact]
    public async Task FinishCapturesFailures()
    {
        var wizard = new TestWizard { ShouldThrow = true };
        wizard.Step1Value = "ok";
        wizard.Next();
        wizard.Next();

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.True(wizard.HasCompleted);
        Assert.True(wizard.HasFailed);
        Assert.Contains("boom", wizard.ResultMessage);
    }

    [Fact]
    public void CancelFiresEventWhenNotRunning()
    {
        var wizard = new TestWizard();
        var fired = false;
        wizard.Cancelled += (_, _) => fired = true;

        wizard.Cancel();

        Assert.True(fired);
    }

    [Fact]
    public void BackBlockedAtFirstStep()
    {
        var wizard = new TestWizard();

        wizard.Back();

        Assert.Equal(0, wizard.CurrentStepIndex);
    }

    private sealed class TestWizard : WizardViewModelBase
    {
        public TestWizard()
            : base(
                "Test",
                "Test wizard",
                new[]
                {
                    new WizardStepViewModel("intro", "Intro", "Welcome"),
                    new WizardStepViewModel("input", "Input", "Provide a value"),
                    new WizardStepViewModel("confirm", "Confirm", "Review and run"),
                })
        {
        }

        private string _step1Value = string.Empty;
        public string Step1Value
        {
            get => _step1Value;
            set
            {
                if (_step1Value != value)
                {
                    _step1Value = value;
                    RaiseValidationChanged();
                }
            }
        }

        public bool ShouldThrow { get; set; }

        public int ExecutedCount { get; private set; }

        protected override bool IsStepValid(int index)
            => index switch
            {
                1 => !string.IsNullOrWhiteSpace(Step1Value),
                _ => true,
            };

        protected override Task<WizardResult> ExecuteAsync(IProgress<string> log, CancellationToken cancellationToken)
        {
            ExecutedCount++;
            log.Report("running");
            if (ShouldThrow)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(new WizardResult(true, "ok"));
        }
    }
}
