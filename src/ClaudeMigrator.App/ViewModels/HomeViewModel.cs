using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeMigrator.App.ViewModels;

public sealed class WorkflowCardViewModel : ViewModelBase
{
    public WorkflowCardViewModel(string workflowId, string title, string subtitle, string description, string accent)
    {
        WorkflowId = workflowId;
        Title = title;
        Subtitle = subtitle;
        Description = description;
        Accent = accent;
    }

    public string WorkflowId { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string Description { get; }

    public string Accent { get; }
}

public sealed partial class HomeViewModel : ViewModelBase
{
    public const string WebRecreationWorkflowId = "web-recreation";
    public const string CoworkSessionsWorkflowId = "cowork-sessions";
    public const string LocalBundleWorkflowId = "local-bundle";
    public const string RemoteBundleWorkflowId = "remote-bundle";

    private readonly Action<string> _openLogs;
    private readonly Action<string> _openSessions;

    public HomeViewModel(Action<string> openLogs, Action<string> openSessions, string debugLogPath)
    {
        _openLogs = openLogs ?? throw new ArgumentNullException(nameof(openLogs));
        _openSessions = openSessions ?? throw new ArgumentNullException(nameof(openSessions));
        DebugLogPath = debugLogPath;

        Workflows = new ReadOnlyCollection<WorkflowCardViewModel>(new[]
        {
            new WorkflowCardViewModel(
                WebRecreationWorkflowId,
                "Recreate Claude Web Export",
                "Most important. Use this to move your claude.ai projects, chats, and docs.",
                "Reads your claude.ai data export ZIP and recreates each project, conversation, and project doc under the new Claude account via an attached Edge debugging session.",
                "#3b82f6"),
            new WorkflowCardViewModel(
                CoworkSessionsWorkflowId,
                "Move Cowork & Code Sessions",
                "Make your Cowork tasks and Code session history appear under the new account.",
                "Copies the Claude Desktop local-agent-mode-sessions and claude-code-sessions data from the old account folder to the new account folder. Close Claude Desktop first.",
                "#10b981"),
            new WorkflowCardViewModel(
                LocalBundleWorkflowId,
                "Local Profile Bundle",
                "Snapshot ~/.claude into a portable bundle and restore it elsewhere.",
                "Captures the Claude profile under a source home into a portable ZIP, then unpacks it under a destination home folder. Good for cross-machine migrations.",
                "#f59e0b"),
            new WorkflowCardViewModel(
                RemoteBundleWorkflowId,
                "Remote Bundle Command",
                "Generate a command to build a bundle on another machine.",
                "Generates an ssh or wsman command line you can paste on a remote machine to capture its ~/.claude into a bundle.",
                "#a855f7"),
        });
    }

    public IReadOnlyList<WorkflowCardViewModel> Workflows { get; }

    public string DebugLogPath { get; }

    public event EventHandler<string>? WorkflowSelected;

    [RelayCommand]
    private void SelectWorkflow(string? workflowId)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            return;
        }

        WorkflowSelected?.Invoke(this, workflowId);
    }

    [RelayCommand]
    private void OpenLogs() => _openLogs("logs");

    [RelayCommand]
    private void OpenSessions() => _openSessions("sessions");
}
