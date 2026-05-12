using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClaudeMigrator.Core.Utilities;
using ClaudeMigrator.Core.Web;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeMigrator.App.ViewModels.Wizards;

public sealed partial class WebRecreationWizardViewModel : WizardViewModelBase
{
    public delegate Task<ClaudeWebRecreationResult> RecreateDelegate(
        ClaudeWebRecreationOptions options,
        Action<string> log,
        CancellationToken cancellationToken);

    private readonly RecreateDelegate _recreate;

    public WebRecreationWizardViewModel(RecreateDelegate recreate, string? defaultOutputFolder = null)
        : base(
            title: "Recreate Claude Web Export",
            subtitle: "Recreate projects, conversations, and transcript docs in your new Claude account through an attached Edge session.",
            steps: CreateSteps())
    {
        _recreate = recreate ?? throw new ArgumentNullException(nameof(recreate));
        DefaultOutputFolder = string.IsNullOrWhiteSpace(defaultOutputFolder)
            ? Path.Combine(Directory.GetCurrentDirectory(), "runtime", "web_recreation")
            : defaultOutputFolder;
        OutputManifestPath = SuggestedManifestPath();
    }

    private static IReadOnlyList<WizardStepViewModel> CreateSteps() =>
        new[]
        {
            new WizardStepViewModel(
                "intro",
                "Overview",
                "Start an Edge profile with remote debugging logged into the new Claude account, then point this wizard at your claude.ai export ZIP. Each project, chat, and doc will be recreated under the new account."),
            new WizardStepViewModel(
                "export",
                "Export ZIP",
                "Select the official Claude data export ZIP downloaded from claude.ai/settings/data-privacy-controls."),
            new WizardStepViewModel(
                "edge",
                "Edge debug URL",
                "Provide the Edge remote debugging URL. Use Start-LiveClaudeEdge.ps1 to launch a dedicated debug profile."),
            new WizardStepViewModel(
                "output",
                "Output manifest",
                "Where to write the recreation manifest. Used later to verify the run with --verify-web-recreation."),
            new WizardStepViewModel(
                "confirm",
                "Confirm",
                "Review settings. Toggle dry run to validate without writing anything to Claude."),
            new WizardStepViewModel(
                "run",
                "Run",
                "Execute the recreation. Progress and counts appear below."),
        };

    public string DefaultOutputFolder { get; }

    [ObservableProperty]
    private string exportZipPath = string.Empty;

    [ObservableProperty]
    private string edgeDebugUrl = "http://127.0.0.1:9222";

    [ObservableProperty]
    private string outputManifestPath = string.Empty;

    [ObservableProperty]
    private string transcriptProjectName = string.Empty;

    [ObservableProperty]
    private string model = string.Empty;

    [ObservableProperty]
    private bool dryRun;

    [ObservableProperty]
    private int createdProjectCount;

    [ObservableProperty]
    private int existingProjectCount;

    [ObservableProperty]
    private int createdConversationCount;

    [ObservableProperty]
    private int existingConversationCount;

    [ObservableProperty]
    private int createdDocCount;

    [ObservableProperty]
    private int existingDocCount;

    [ObservableProperty]
    private int failedOperationCount;

    partial void OnExportZipPathChanged(string value) => RaiseValidationChanged();

    partial void OnEdgeDebugUrlChanged(string value) => RaiseValidationChanged();

    partial void OnOutputManifestPathChanged(string value) => RaiseValidationChanged();

    protected override bool IsStepValid(int index)
        => index switch
        {
            1 => !string.IsNullOrWhiteSpace(ExportZipPath) && File.Exists(ExportZipPath),
            2 => !string.IsNullOrWhiteSpace(EdgeDebugUrl)
                 && Uri.TryCreate(EdgeDebugUrl, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
            3 => !string.IsNullOrWhiteSpace(OutputManifestPath),
            _ => true,
        };

    protected override async Task<WizardResult> ExecuteAsync(IProgress<string> log, CancellationToken cancellationToken)
    {
        var options = new ClaudeWebRecreationOptions(
            ExportZipPath: Path.GetFullPath(ExportZipPath),
            EdgeDebugUrl: EdgeDebugUrl,
            OutputManifestPath: Path.GetFullPath(OutputManifestPath),
            DryRun: DryRun,
            TranscriptProjectName: string.IsNullOrWhiteSpace(TranscriptProjectName) ? null : TranscriptProjectName,
            Model: string.IsNullOrWhiteSpace(Model) ? null : Model);

        var result = await _recreate(options, line => log.Report(line), cancellationToken).ConfigureAwait(false);

        CreatedProjectCount = result.CreatedProjectCount;
        ExistingProjectCount = result.ExistingProjectCount;
        CreatedConversationCount = result.CreatedConversationCount;
        ExistingConversationCount = result.ExistingConversationCount;
        CreatedDocCount = result.CreatedDocCount;
        ExistingDocCount = result.ExistingDocCount;
        FailedOperationCount = result.FailedOperationCount;

        var success = result.FailedOperationCount == 0;
        var message = DryRun
            ? $"Dry run complete. Would create {result.CreatedProjectCount} projects, {result.CreatedConversationCount} conversations, {result.CreatedDocCount} docs."
            : $"Created {result.CreatedProjectCount} projects, {result.CreatedConversationCount} conversations, {result.CreatedDocCount} docs. Existing: {result.ExistingProjectCount}/{result.ExistingConversationCount}/{result.ExistingDocCount}. Failed: {result.FailedOperationCount}.";

        return new WizardResult(success, message);
    }

    private string SuggestedManifestPath()
        => Path.Combine(DefaultOutputFolder, $"claude_web_recreation_{PathUtils.TimestampTag()}.json");
}
