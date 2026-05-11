# Claude Migrator

Windows desktop app for migrating Claude account content into a portable local bundle and, where possible, automating the Claude web UI with Playwright.

## What It Does

- Shows a dark-themed GUI with per-step progress and a live log panel.
- Lets you choose a source mode:
  - local profile snapshot from `~/.claude`
  - existing Claude export ZIP
- Lets you choose one or both targets:
  - Claude
  - Codex
- Opens Edge for the source account and Firefox for the destination account.
- Saves `storage_state` snapshots for both browsers.
- Triggers the official Claude export flow.
- Finds and validates the downloaded ZIP.
- Builds a local-only Claude bundle that preserves the source machine, environment, and account metadata.
- Can restore the local bundle back under the selected target profile roots:
  - `~/.claude`
  - `~/.codex`
- Lets you maintain a list of remote machines and generate the local-export command for `ssh` or `wsman`.
- Runs the local Codex processing step to generate:
  - `memory/memory.json`
  - `projects/[ProjectName]/instructions.md`
  - `projects/[ProjectName]/knowledge_summary.md`
  - `projects/[ProjectName]/import_blueprint.md`
  - `conversations/[chat_title]/chat.md`
  - `conversations/[chat_title]/chat.json`
  - `artifacts/extracted_code/`
  - `manifest.json`
- Can import memory, recreate projects, and seed continuation chats on a best-effort basis.
- Can export all artifacts to a clean portable ZIP independently of the browser steps.

## Important Warning

This app can automate claude.ai with Playwright. That is higher-risk and may break site rules or UI flows.

Use it only on your own accounts.

The safest and most valuable part of the app is the local Codex processing and portable export generation.

## Run It

```bat
launch_claude_migration.bat
```

Or, from this folder:

```powershell
dotnet run --project src/ClaudeMigrator.App/ClaudeMigrator.App.csproj
```

## First Run Checklist

1. Start the app.
2. Pick a source mode:
   - `Local profile snapshot` to bundle the current `~/.claude`
   - `Source ZIP` to use an existing export ZIP
3. Choose the target app(s) with the `Claude` and `Codex` checkboxes.
4. Use `Browse...` only in `Source ZIP` mode, or let the app discover the newest ZIP in your Downloads folder.
5. Click `Start Full Migration`.
6. When the browser setup step pauses, sign in to both Claude accounts and click `Save Sessions & Continue`.

## Bundles

The `Build Source Bundle` button runs the selected source path without the browser automation steps when you are in local snapshot mode.

The local snapshot bundle includes a top-level `claude_local_bundle_YYYYMMDD_HHMM/` folder with:

- `manifest.json`
- `metadata/source_environment.json`
- `metadata/source_account.json`
- `metadata/restore_plan.json`
- `memory/memory.json`
- `projects/[ProjectName]/...`
- `conversations/[chat_title]/...`
- `artifacts/extracted_code/`
- `source/home/.claude/`
- `source/home/.claude.json`

The manifest includes import guidance for:

- another Claude account
- a local Codex instance
- GitHub Copilot or Microsoft Copilot

The local bundle also preserves the source machine name, host, user, connection method, working directory, and a safe account summary so the bundle can be written back under a new account later.

## Remote Machines

Use the Remote Machines panel to manage source hosts that you want to export from over `ssh` or `wsman`.

- Save the host, username, repo root, and connection method.
- Select a saved row to edit it.
- Click `Copy Command` to copy the local-only export command for that machine.
- Remote machine definitions are stored under `migration_data/remote_machines.json`.

## Tests

Run the xUnit unit and integration suite with:

```powershell
dotnet test tests/ClaudeMigrator.Tests/ClaudeMigrator.Tests.csproj -v minimal
```

The browser integration tests use Playwright and real browser binaries. After the first build, install the browsers if needed with the generated Playwright script:

```powershell
pwsh .\tests\ClaudeMigrator.Tests\bin\Debug\net10.0\playwright.ps1 install chromium firefox msedge
```

## Manual Steps You Still Need

- First login for each Claude account.
- Any selector updates if Claude changes its UI.
- Setting the destination account's browser and Windows default browser preferences.

## Packaging

To build a single-file executable, use `dotnet publish` for the target runtime.

Example:

```powershell
dotnet publish src/ClaudeMigrator.App/ClaudeMigrator.App.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

If you package the app, make sure the `migration_data/` runtime folder is preserved beside the executable or created on first run.
