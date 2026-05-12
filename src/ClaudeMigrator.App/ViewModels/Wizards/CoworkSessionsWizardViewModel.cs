using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeMigrator.Core.Local;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeMigrator.App.ViewModels.Wizards;

public sealed partial class CoworkSessionsWizardViewModel : WizardViewModelBase
{
    public const string LocalAgentSessionsSubfolder = "local-agent-mode-sessions";
    public const string ClaudeCodeSessionsSubfolder = "claude-code-sessions";

    public delegate LocalAgentSessionsMigrationResult MigrateDelegate(LocalAgentSessionsMigrationOptions options, Action<string> log);

    private readonly MigrateDelegate _migrate;

    public CoworkSessionsWizardViewModel(
        IReadOnlyList<ClaudeOauthAccount> availableAccounts,
        MigrateDelegate migrate,
        string? claudeAppDataRoot = null)
        : base(
            title: "Move Cowork & Code Sessions",
            subtitle: "Move Claude Desktop Cowork tasks and Code sessions to a new account.",
            steps: CreateSteps())
    {
        _migrate = migrate ?? throw new ArgumentNullException(nameof(migrate));
        Accounts = new ObservableCollection<ClaudeOauthAccount>(availableAccounts ?? Array.Empty<ClaudeOauthAccount>());
        ClaudeAppDataRoot = string.IsNullOrWhiteSpace(claudeAppDataRoot)
            ? DefaultClaudeAppDataRoot()
            : claudeAppDataRoot;

        if (Accounts.Count > 0)
        {
            TargetAccount = Accounts[0];
            SourceAccount = Accounts.Count > 1 ? Accounts[1] : Accounts[0];
        }
    }

    private static IReadOnlyList<WizardStepViewModel> CreateSteps() =>
        new[]
        {
            new WizardStepViewModel(
                "intro",
                "Overview",
                "Move your Cowork tasks (under local-agent-mode-sessions) and your Code sessions (under claude-code-sessions) from the old account folder to the new account folder. Close Claude Desktop before running."),
            new WizardStepViewModel(
                "accounts",
                "Pick accounts",
                "Choose the source (old) and target (new) accounts. Detected from .claude.json and ~/.claude/backups."),
            new WizardStepViewModel(
                "confirm",
                "Confirm",
                "Review what will be moved. Use a dry run first if you want to validate the file set."),
            new WizardStepViewModel(
                "run",
                "Run",
                "Execute the migration. Output appears below."),
        };

    public static string DefaultClaudeAppDataRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");

    public string ClaudeAppDataRoot { get; }

    public ObservableCollection<ClaudeOauthAccount> Accounts { get; }

    [ObservableProperty]
    private ClaudeOauthAccount? sourceAccount;

    [ObservableProperty]
    private ClaudeOauthAccount? targetAccount;

    [ObservableProperty]
    private bool dryRun;

    [ObservableProperty]
    private bool overwrite;

    [ObservableProperty]
    private int totalCopiedCount;

    [ObservableProperty]
    private int totalSkippedCount;

    [ObservableProperty]
    private int totalFailedCount;

    [ObservableProperty]
    private long totalBytesCopied;

    public string SourceAgentModeSessionsPath => BuildSessionsPath(LocalAgentSessionsSubfolder, SourceAccount);

    public string TargetAgentModeSessionsPath => BuildSessionsPath(LocalAgentSessionsSubfolder, TargetAccount);

    public string SourceClaudeCodeSessionsPath => BuildSessionsPath(ClaudeCodeSessionsSubfolder, SourceAccount);

    public string TargetClaudeCodeSessionsPath => BuildSessionsPath(ClaudeCodeSessionsSubfolder, TargetAccount);

    partial void OnSourceAccountChanged(ClaudeOauthAccount? value)
    {
        OnPropertyChanged(nameof(SourceAgentModeSessionsPath));
        OnPropertyChanged(nameof(SourceClaudeCodeSessionsPath));
        RaiseValidationChanged();
    }

    partial void OnTargetAccountChanged(ClaudeOauthAccount? value)
    {
        OnPropertyChanged(nameof(TargetAgentModeSessionsPath));
        OnPropertyChanged(nameof(TargetClaudeCodeSessionsPath));
        RaiseValidationChanged();
    }

    protected override bool IsStepValid(int index)
        => index switch
        {
            1 => SourceAccount is not null
                 && TargetAccount is not null
                 && !string.Equals(SourceAccount.AccountUuid, TargetAccount.AccountUuid, StringComparison.OrdinalIgnoreCase)
                 && !string.IsNullOrWhiteSpace(SourceAccount.OrganizationUuid)
                 && !string.IsNullOrWhiteSpace(TargetAccount.OrganizationUuid),
            _ => true,
        };

    protected override Task<WizardResult> ExecuteAsync(IProgress<string> log, CancellationToken cancellationToken)
    {
        if (SourceAccount is null || TargetAccount is null)
        {
            return Task.FromResult(new WizardResult(false, "Source and target accounts must be selected."));
        }

        var totals = (Copied: 0, Skipped: 0, Failed: 0, Bytes: 0L);
        var anySucceeded = false;

        foreach (var subfolder in new[] { LocalAgentSessionsSubfolder, ClaudeCodeSessionsSubfolder })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionsRoot = Path.Combine(ClaudeAppDataRoot, subfolder);
            log.Report($"Migrating {subfolder} under {sessionsRoot}");

            try
            {
                var result = _migrate(
                    new LocalAgentSessionsMigrationOptions(
                        SourceAccountUuid: SourceAccount.AccountUuid,
                        SourceOrgUuid: SourceAccount.OrganizationUuid,
                        TargetAccountUuid: TargetAccount.AccountUuid,
                        TargetOrgUuid: TargetAccount.OrganizationUuid,
                        SessionsRoot: sessionsRoot,
                        DryRun: DryRun,
                        Overwrite: Overwrite),
                    line => log.Report(line));

                totals.Copied += result.CopiedFileCount;
                totals.Skipped += result.SkippedFileCount;
                totals.Failed += result.FailedFileCount;
                totals.Bytes += result.TotalBytesCopied;
                anySucceeded = true;

                log.Report($"{subfolder}: copied {result.CopiedFileCount}, skipped {result.SkippedFileCount}, failed {result.FailedFileCount}.");
            }
            catch (DirectoryNotFoundException ex)
            {
                log.Report($"{subfolder}: source folder missing, skipped. {ex.Message}");
            }
            catch (Exception ex)
            {
                log.Report($"{subfolder}: failed. {ex.Message}");
                totals.Failed++;
            }
        }

        TotalCopiedCount = totals.Copied;
        TotalSkippedCount = totals.Skipped;
        TotalFailedCount = totals.Failed;
        TotalBytesCopied = totals.Bytes;

        if (!anySucceeded)
        {
            return Task.FromResult(new WizardResult(false, "No source folders were found for the selected accounts."));
        }

        var message = DryRun
            ? $"Dry run complete. Would copy {totals.Copied} files ({totals.Bytes / 1024} KB). Failed: {totals.Failed}."
            : $"Migrated {totals.Copied} files ({totals.Bytes / 1024} KB). Skipped {totals.Skipped}. Failed {totals.Failed}.";

        var success = anySucceeded && totals.Failed == 0;
        return Task.FromResult(new WizardResult(success, message));
    }

    private string BuildSessionsPath(string subfolder, ClaudeOauthAccount? account)
    {
        if (account is null)
        {
            return string.Empty;
        }

        return Path.Combine(ClaudeAppDataRoot, subfolder, account.AccountUuid, account.OrganizationUuid);
    }
}
