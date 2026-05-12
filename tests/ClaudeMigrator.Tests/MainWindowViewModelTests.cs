using ClaudeMigrator.Core.Migration;
using ClaudeMigrator.Core.Models;
using ClaudeMigrator.Core.Paths;
using ClaudeMigrator.App.ViewModels;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void ViewModelDefaultsExposeSourceControlsAndRemoteMachineEditor()
    {
        using var workspace = new TestWorkspace();
        using var controller = new MigrationController(new AppPaths(workspace.Root).Ensure());
        var viewModel = new MainWindowViewModel(controller);

        Assert.Contains("ssh", viewModel.ConnectionMethods, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("wsman", viewModel.ConnectionMethods, StringComparer.OrdinalIgnoreCase);
        Assert.True(viewModel.TargetClaude);
        Assert.True(viewModel.TargetCodex);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), viewModel.SourceHome);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), viewModel.DestinationHome);
        Assert.Equal(Environment.MachineName, viewModel.SourceMachineName);
        Assert.Equal(Environment.MachineName, viewModel.SourceHost);
        Assert.Equal(Environment.UserName, viewModel.SourceUser);
        Assert.Equal(string.Empty, viewModel.SourceAccount);
        Assert.Equal(string.Empty, viewModel.TargetAccount);
    }

    [Fact]
    public void ViewModelCanOpenLogsAndSessionsFolders()
    {
        using var workspace = new TestWorkspace();
        using var controller = new MigrationController(new AppPaths(workspace.Root).Ensure());
        var openedFolders = new List<string>();
        var viewModel = new MainWindowViewModel(controller, openedFolders.Add);

        viewModel.OpenLogsFolderCommand.Execute(null);
        viewModel.OpenSessionsFolderCommand.Execute(null);

        Assert.Collection(
            openedFolders,
            path => Assert.Equal(controller.Paths.LogsDir, path),
            path => Assert.Equal(controller.Paths.SessionsDir, path));
    }

    [Fact]
    public void ViewModelCanSaveAndRemoveRemoteMachines()
    {
        using var workspace = new TestWorkspace();
        using var controller = new MigrationController(new AppPaths(workspace.Root).Ensure());
        var viewModel = new MainWindowViewModel(controller);

        viewModel.RemoteMachineName = "Lab Box";
        viewModel.RemoteMachineHost = "lab.example.com";
        viewModel.RemoteMachineMethod = "wsman";
        viewModel.RemoteMachineUser = "kingd";
        viewModel.RemoteMachineRepoRoot = @"F:\GitHub\ClaudeMigrator";
        viewModel.RemoteMachinePort = "5985";
        viewModel.RemoteMachineNotes = "Primary remote source";

        viewModel.SaveRemoteMachineCommand.Execute(null);

        Assert.Single(viewModel.RemoteMachines);
        Assert.NotNull(viewModel.SelectedRemoteMachine);
        Assert.Equal("lab-box", viewModel.SelectedRemoteMachine!.MachineId);
        Assert.Equal("lab.example.com", viewModel.SelectedRemoteMachine.Host);
        Assert.Equal("wsman", viewModel.SelectedRemoteMachine.ConnectionMethod);
        Assert.Single(controller.LoadRemoteMachines());

        viewModel.RemoveRemoteMachineCommand.Execute(null);

        Assert.Empty(viewModel.RemoteMachines);
        Assert.Empty(controller.LoadRemoteMachines());
        Assert.Null(viewModel.SelectedRemoteMachine);
        Assert.Equal(string.Empty, viewModel.RemoteMachineName);
        Assert.Equal(string.Empty, viewModel.RemoteMachineHost);
    }

    [Fact]
    public void ViewModelNormalizesPathSelections()
    {
        using var workspace = new TestWorkspace();
        using var controller = new MigrationController(new AppPaths(workspace.Root).Ensure());
        var viewModel = new MainWindowViewModel(controller);

        var selectedExport = Path.Combine(workspace.Root, "export.zip");
        File.WriteAllText(selectedExport, "zip", System.Text.Encoding.UTF8);
        viewModel.SetSelectedExportZipPath(selectedExport);
        viewModel.SetSourceHomePath(workspace.Root);
        viewModel.SetDestinationHomePath(workspace.Root);
        viewModel.SetSourceRepoRootPath(workspace.Root);

        Assert.Equal(Path.GetFullPath(selectedExport), viewModel.SelectedExportZip);
        Assert.Equal(Path.GetFullPath(selectedExport), controller.SelectedExportZip);
        Assert.Equal(Path.GetFullPath(workspace.Root), viewModel.SourceHome);
        Assert.Equal(Path.GetFullPath(workspace.Root), viewModel.DestinationHome);
        Assert.Equal(Path.GetFullPath(workspace.Root), viewModel.SourceRepoRoot);
    }

    [Fact]
    public void ViewModelSwitchesSourceModesAndUpdatesController()
    {
        using var workspace = new TestWorkspace();
        using var controller = new MigrationController(new AppPaths(workspace.Root).Ensure());
        var viewModel = new MainWindowViewModel(controller);

        viewModel.IsLocalSourceMode = true;

        Assert.True(viewModel.IsLocalSourceMode);
        Assert.False(viewModel.IsZipSourceMode);
        Assert.Equal(SourceMode.LocalSnapshot, controller.SourceMode);

        viewModel.IsZipSourceMode = true;

        Assert.True(viewModel.IsZipSourceMode);
        Assert.False(viewModel.IsLocalSourceMode);
        Assert.Equal(SourceMode.Zip, controller.SourceMode);
    }
}
