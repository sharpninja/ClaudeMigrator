# ClaudeMigrator

ClaudeMigrator is a .NET 10 and Avalonia 12 desktop app for migrating Claude account data into portable local bundles and restoring that data into Claude, Codex, or both.

The UI includes source, destination, and target account text boxes so the local bundle can preserve who the source was and where the restored data should land.

## Layout

- `src/ClaudeMigrator.Core` contains the exporter, restore, browser, and remote-target logic.
- `src/ClaudeMigrator.App` contains the Avalonia UI and the command-line entry point.
- `tests/ClaudeMigrator.Tests` contains the xUnit unit and integration suite, including Playwright browser coverage.
- `claude_migration_app/README.md` contains app-local notes and the Windows launcher entry point.

## Build

```powershell
dotnet build ClaudeMigrator.slnx -v minimal
```

## Test

```powershell
dotnet test tests/ClaudeMigrator.Tests/ClaudeMigrator.Tests.csproj -v minimal
```

## Run

```powershell
dotnet run --project src/ClaudeMigrator.App/ClaudeMigrator.App.csproj
```

On Windows, you can also launch:

```bat
claude_migration_app\launch_claude_migration.bat
```

## Browser Integration Tests

The browser integration tests use Playwright and real browser binaries. After the first build, install the browsers if needed:

```powershell
pwsh .\tests\ClaudeMigrator.Tests\bin\Debug\net10.0\playwright.ps1 install chromium firefox msedge
```

Two live Claude tests are also present, but they are skipped unless you opt in with:

```powershell
$env:CLAUDEMIGRATOR_RUN_LIVE_CLAUDE = "1"
$env:CLAUDEMIGRATOR_LIVE_EDGE_STORAGE_STATE = "C:\path\to\edge.storage.json"
$env:CLAUDEMIGRATOR_LIVE_EDGE_DEBUG_URL = "http://127.0.0.1:9222"
$env:CLAUDEMIGRATOR_LIVE_EDGE_PROFILE_ROOT = "C:\path\to\edge-test-profile"
```

The live export test needs either a live Claude export ZIP or the Edge storage state file. The live Edge import test attaches to a Chromium/Edge browser started with remote debugging at the URL above, plus either a live export ZIP or the Edge storage state file.
The import verification now restarts that dedicated Edge profile, reattaches, and then re-exports Claude so the test can prove the imported memory survives a fresh browser session.
That import flow uses Claude's data privacy controls page and, on the current UI, completes the memory step by clicking `Add to memory` after `Start import`.

Claude's built-in memory import rewrites submitted text into Claude-managed memory sections. It is useful for target-account memory bootstrap, but it is not an exact artifact store. Exact web-data migration is handled by the web recreation command, which recreates/matches projects and conversations and writes exact transcript, source-memory, project-memory, and project-doc Markdown documents into Claude projects with SHA-256 verification.

```powershell
dotnet run --project src/ClaudeMigrator.App/ClaudeMigrator.App.csproj -- `
  --recreate-web-export `
  --export-zip "C:\path\to\claude-export.zip" `
  --edge-debug-url "http://127.0.0.1:9222" `
  --output-manifest ".\runtime\web_recreation\manifest.json"

dotnet run --project src/ClaudeMigrator.App/ClaudeMigrator.App.csproj -- `
  --verify-web-recreation `
  --manifest ".\runtime\web_recreation\manifest.json" `
  --edge-debug-url "http://127.0.0.1:9222" `
  --output-verification ".\runtime\web_recreation\verification.json"
```

There is also a browser-free live local test that uses your real `~/.claude` profile and restores into a temp destination home. Enable it with:

```powershell
$env:CLAUDEMIGRATOR_RUN_LIVE_LOCAL = "1"
```

You can override the source path with `CLAUDEMIGRATOR_LIVE_LOCAL_SOURCE_HOME`, but the default is your current Windows user profile.

Helper:

```powershell
.\Start-LiveClaudeEdge.ps1
```

That script opens a dedicated test profile, enables remote debugging on port `9222`, and sets `CLAUDEMIGRATOR_LIVE_EDGE_DEBUG_URL` for the current shell.
It opens Claude directly on the data privacy controls page so the live import flow starts on the right screen.
It also sets `CLAUDEMIGRATOR_LIVE_EDGE_PROFILE_ROOT` so the test can restart the dedicated browser profile during the post-import verification pass.

## Runtime Data

The app creates its runtime data under `migration_data/` at startup. That folder is generated locally and intentionally ignored by git.
