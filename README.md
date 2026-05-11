# ClaudeMigrator

ClaudeMigrator is a .NET 10 and Avalonia 12 desktop app for migrating Claude account data into portable local bundles and restoring that data into Claude, Codex, or both.

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

## Runtime Data

The app creates its runtime data under `migration_data/` at startup. That folder is generated locally and intentionally ignored by git.
