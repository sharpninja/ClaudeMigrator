using System.Collections.Generic;
using System.Linq;
using ClaudeMigrator.App.ViewModels;

namespace ClaudeMigrator.Tests;

public sealed class HomeViewModelTests
{
    [Fact]
    public void ExposesFourWorkflowsInExpectedOrder()
    {
        var vm = new HomeViewModel(_ => { }, _ => { }, "log.txt");

        Assert.Equal(4, vm.Workflows.Count);
        Assert.Equal(HomeViewModel.WebRecreationWorkflowId, vm.Workflows[0].WorkflowId);
        Assert.Equal(HomeViewModel.CoworkSessionsWorkflowId, vm.Workflows[1].WorkflowId);
        Assert.Equal(HomeViewModel.LocalBundleWorkflowId, vm.Workflows[2].WorkflowId);
        Assert.Equal(HomeViewModel.RemoteBundleWorkflowId, vm.Workflows[3].WorkflowId);
    }

    [Fact]
    public void SelectWorkflowRaisesEventWithId()
    {
        var vm = new HomeViewModel(_ => { }, _ => { }, "log.txt");
        var captured = new List<string>();
        vm.WorkflowSelected += (_, id) => captured.Add(id);

        vm.SelectWorkflowCommand.Execute(HomeViewModel.CoworkSessionsWorkflowId);

        Assert.Single(captured);
        Assert.Equal(HomeViewModel.CoworkSessionsWorkflowId, captured[0]);
    }

    [Fact]
    public void SelectWorkflowIgnoresEmptyId()
    {
        var vm = new HomeViewModel(_ => { }, _ => { }, "log.txt");
        var fired = false;
        vm.WorkflowSelected += (_, _) => fired = true;

        vm.SelectWorkflowCommand.Execute(string.Empty);

        Assert.False(fired);
    }

    [Fact]
    public void OpenLogsAndSessionsInvokeProvidedDelegates()
    {
        var opened = new List<string>();
        var vm = new HomeViewModel(opened.Add, opened.Add, "log.txt");

        vm.OpenLogsCommand.Execute(null);
        vm.OpenSessionsCommand.Execute(null);

        Assert.Equal(new[] { "logs", "sessions" }, opened.ToArray());
    }
}
