using System.Collections.Generic;
using System.Linq;
using ClaudeMigrator.App.ViewModels;
using ClaudeMigrator.App.ViewModels.Wizards;
using ClaudeMigrator.Core.Migration;
using ClaudeMigrator.Core.Paths;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void StartsOnHomeView()
    {
        using var workspace = new TestWorkspace();
        using var controller = new MigrationController(new AppPaths(workspace.Root).Ensure());
        var vm = new MainWindowViewModel(controller);

        Assert.True(vm.IsOnHome);
        Assert.IsType<HomeViewModel>(vm.CurrentView);
    }

    [Fact]
    public void SelectingWebRecreationWorkflowSwitchesToWebWizard()
    {
        using var workspace = new TestWorkspace();
        using var controller = new MigrationController(new AppPaths(workspace.Root).Ensure());
        var vm = new MainWindowViewModel(controller);

        ((HomeViewModel)vm.CurrentView!).SelectWorkflowCommand.Execute(HomeViewModel.WebRecreationWorkflowId);

        Assert.True(vm.IsOnWizard);
        Assert.IsType<WebRecreationWizardViewModel>(vm.CurrentView);
    }

    [Fact]
    public void SelectingCoworkWorkflowSwitchesToCoworkWizard()
    {
        using var workspace = new TestWorkspace();
        using var controller = new MigrationController(new AppPaths(workspace.Root).Ensure());
        var vm = new MainWindowViewModel(controller);

        ((HomeViewModel)vm.CurrentView!).SelectWorkflowCommand.Execute(HomeViewModel.CoworkSessionsWorkflowId);

        Assert.IsType<CoworkSessionsWizardViewModel>(vm.CurrentView);
    }

    [Fact]
    public void SelectingLocalBundleWorkflowSwitchesToLocalBundleWizard()
    {
        using var workspace = new TestWorkspace();
        using var controller = new MigrationController(new AppPaths(workspace.Root).Ensure());
        var vm = new MainWindowViewModel(controller);

        ((HomeViewModel)vm.CurrentView!).SelectWorkflowCommand.Execute(HomeViewModel.LocalBundleWorkflowId);

        Assert.IsType<LocalBundleWizardViewModel>(vm.CurrentView);
    }

    [Fact]
    public void SelectingRemoteBundleWorkflowSwitchesToRemoteBundleWizard()
    {
        using var workspace = new TestWorkspace();
        using var controller = new MigrationController(new AppPaths(workspace.Root).Ensure());
        var vm = new MainWindowViewModel(controller);

        ((HomeViewModel)vm.CurrentView!).SelectWorkflowCommand.Execute(HomeViewModel.RemoteBundleWorkflowId);

        Assert.IsType<RemoteBundleWizardViewModel>(vm.CurrentView);
    }

    [Fact]
    public void CancellingWizardReturnsToHome()
    {
        using var workspace = new TestWorkspace();
        using var controller = new MigrationController(new AppPaths(workspace.Root).Ensure());
        var vm = new MainWindowViewModel(controller);
        ((HomeViewModel)vm.CurrentView!).SelectWorkflowCommand.Execute(HomeViewModel.RemoteBundleWorkflowId);
        var wizard = (WizardViewModelBase)vm.CurrentView!;

        wizard.Cancel();

        Assert.True(vm.IsOnHome);
    }

    [Fact]
    public void NavigateHomeFromAnyViewReturnsHome()
    {
        using var workspace = new TestWorkspace();
        using var controller = new MigrationController(new AppPaths(workspace.Root).Ensure());
        var vm = new MainWindowViewModel(controller);
        ((HomeViewModel)vm.CurrentView!).SelectWorkflowCommand.Execute(HomeViewModel.LocalBundleWorkflowId);

        vm.NavigateHomeCommand.Execute(null);

        Assert.True(vm.IsOnHome);
    }

    [Fact]
    public void OpenLogsAndSessionsAreReachableFromHome()
    {
        using var workspace = new TestWorkspace();
        using var controller = new MigrationController(new AppPaths(workspace.Root).Ensure());
        var opened = new List<string>();
        var vm = new MainWindowViewModel(controller, opened.Add);
        var home = (HomeViewModel)vm.CurrentView!;

        home.OpenLogsCommand.Execute(null);
        home.OpenSessionsCommand.Execute(null);

        Assert.Equal(new[] { controller.Paths.LogsDir, controller.Paths.SessionsDir }, opened.ToArray());
    }
}
