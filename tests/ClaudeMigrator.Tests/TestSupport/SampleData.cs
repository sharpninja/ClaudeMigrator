using System.IO.Compression;
using System.Text.Json;

namespace ClaudeMigrator.Tests.TestSupport;

internal static class SampleData
{
    public static string CreateSampleExportZip(string root, bool structured = true)
    {
        var archivePath = Path.Combine(root, "sample_export.zip");
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);

        if (structured)
        {
            WriteJson(archive, "conversation.json", new
            {
                title = "Test Chat",
                project_name = "Demo Project",
                id = "conversation-001",
                messages = new object[]
                {
                    new { role = "user", text = "Plan the migration." },
                    new { role = "assistant", text = "Use the exporter and retain memory." },
                },
            });

            WriteJson(archive, "project.json", new
            {
                name = "Demo Project",
                instructions = "Follow the export test plan.",
                knowledge = new { notes = "Remember the portable bundle layout." },
                conversations = new object[]
                {
                    new { title = "Test Chat" },
                },
            });

            WriteJson(archive, "memory.json", new
            {
                title = "Project Memory",
                content = "Keep the portable export self-contained.",
            });
        }
        else
        {
            WriteJson(archive, "unstructured.json", new
            {
                unexpected = new
                {
                    nested = new object[]
                    {
                        "value",
                        new { answer = 42 },
                    },
                },
            });
        }

        using (var stream = archive.CreateEntry("broken.json").Open())
        {
            var broken = new byte[] { (byte)'{', (byte)'n', (byte)'o', (byte)'t', (byte)' ' };
            stream.Write(broken, 0, broken.Length);
        }
        WriteText(archive, "source/code/sample.py", "print('hello from sample')\n");
        WriteText(archive, "source/assets/readme.txt", "not code but present\n");
        WriteText(archive, "source/scripts/cleanup.ps1", "Write-Host 'cleanup'\n");

        return archivePath;
    }

    public static string CreateRichSampleExportZip(string root)
    {
        var archivePath = Path.Combine(root, "sample_export_rich.zip");
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);

        WriteJson(archive, "conversations/test-chat.json", new
        {
            title = "Test Chat",
            project_name = "Demo Project",
            id = "conversation-001",
            messages = new object[]
            {
                new { role = "user", text = "Plan the migration." },
                new { role = "assistant", text = "Use the exporter and retain memory." },
            },
        });

        WriteJson(archive, "conversations/migration-retrospective.json", new
        {
            name = "Migration Retrospective",
            project = "Ops Project",
            thread_id = "conversation-002",
            turns = new object[]
            {
                new { role = "user", content = "Review what was preserved." },
                new { role = "assistant", content = "Chats, projects, memory, and artifacts were all retained." },
            },
        });

        WriteJson(archive, "projects/demo-project.json", new
        {
            name = "Demo Project",
            instructions = "Follow the export test plan.",
            knowledge = new { notes = "Remember the portable bundle layout." },
            conversations = new object[]
            {
                new { title = "Test Chat" },
            },
        });

        WriteJson(archive, "projects/ops-project.json", new
        {
            title = "Ops Project",
            description = "Operational notes for the migration bundle.",
            notes = new { summary = "Preserve the runtime and import plan." },
            related_conversations = new object[]
            {
                new { title = "Migration Retrospective" },
            },
        });

        WriteJson(archive, "memory/project-memory.json", new
        {
            title = "Project Memory",
            content = "Keep the portable export self-contained.",
        });

        WriteJson(archive, "memory/release-notes.json", new
        {
            name = "Release Notes",
            text = "Bundle every Claude artifact, not just a sample slice.",
        });

        using (var stream = archive.CreateEntry("broken.json").Open())
        {
            var broken = new byte[] { (byte)'{', (byte)'n', (byte)'o', (byte)'t', (byte)' ' };
            stream.Write(broken, 0, broken.Length);
        }

        WriteText(archive, "source/code/sample.py", "print('hello from sample')\n");
        WriteText(archive, "source/code/worker.ts", "export const worker = () => 'ready';\n");
        WriteText(archive, "source/assets/readme.txt", "not code but present\n");
        WriteText(archive, "source/scripts/cleanup.ps1", "Write-Host 'cleanup'\n");
        WriteText(archive, "source/docs/notes.md", "# Notes\nKeep the full archive intact.\n");

        return archivePath;
    }

    public static string CreateSampleLocalHome(string root)
    {
        var home = Path.Combine(root, "home");
        var claudeRoot = Path.Combine(home, ".claude");
        Directory.CreateDirectory(claudeRoot);

        File.WriteAllText(Path.Combine(claudeRoot, "CLAUDE.md"), "# Local Claude\n", System.Text.Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(claudeRoot, "history.jsonl"),
            JsonSerializer.Serialize(new { role = "user", text = "Remember the local snapshot." }) + Environment.NewLine,
            System.Text.Encoding.UTF8);

        var alphaProjectRoot = Path.Combine(claudeRoot, "projects", "alpha");
        Directory.CreateDirectory(alphaProjectRoot);
        File.WriteAllText(Path.Combine(alphaProjectRoot, "notes.txt"), "alpha notes\n", System.Text.Encoding.UTF8);

        var memoryRoot = Path.Combine(claudeRoot, "memory");
        Directory.CreateDirectory(memoryRoot);
        File.WriteAllText(
            Path.Combine(memoryRoot, "memory.json"),
            JsonSerializer.Serialize(new { items = new[] { new { title = "Alpha", text = "Keep the source machine metadata." } } }, new JsonSerializerOptions { WriteIndented = true }),
            System.Text.Encoding.UTF8);

        var debugRoot = Path.Combine(claudeRoot, "debug");
        Directory.CreateDirectory(debugRoot);
        File.WriteAllText(Path.Combine(debugRoot, "latest"), "debug marker\n", System.Text.Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(home, ".claude.json"),
            JsonSerializer.Serialize(new
            {
                oauthAccount = new
                {
                    emailAddress = "ninja@thesharp.ninja",
                    displayName = "Sharp Ninja",
                    accountUuid = "account-uuid",
                    organizationName = "Sharp Ninja Org",
                    organizationUuid = "org-uuid",
                },
                projects = new Dictionary<string, object?>
                {
                    ["F:/GitHub/ClaudeMigrator"] = new
                    {
                        name = "ClaudeMigrator",
                    },
                },
            }, new JsonSerializerOptions { WriteIndented = true }),
            System.Text.Encoding.UTF8);

        File.WriteAllText(Path.Combine(home, ".claude.json.backup"), "backup copy\n", System.Text.Encoding.UTF8);
        return home;
    }

    public static string CreateSampleBrowserPage(string root)
    {
        var path = Path.Combine(root, "playwright-fixture.html");
        File.WriteAllText(
            path,
            """
            <!doctype html>
            <html>
            <head>
              <meta charset="utf-8" />
              <title>ClaudeMigrator Playwright Fixture</title>
              <style>
                body { font-family: sans-serif; padding: 24px; }
                input, textarea, button { display: block; margin: 12px 0; }
                textarea { width: 360px; height: 100px; }
              </style>
            </head>
            <body>
              <div id="status">idle</div>
              <input type="text" id="message" aria-label="message" />
              <textarea id="notes" aria-label="notes"></textarea>
              <input type="file" id="hidden-upload" aria-label="hidden upload" style="display:none" />
              <input type="file" id="upload" aria-label="upload" />
              <button id="manage-one" type="button">Manage</button>
              <button id="manage-two" type="button">Manage</button>
              <button id="export" type="button">Export data</button>
              <button id="send" type="button">Send</button>
              <script>
                const status = document.getElementById('status');
                document.getElementById('export').addEventListener('click', () => status.textContent = 'export-clicked');
                document.getElementById('send').addEventListener('click', () => status.textContent = 'send-clicked');
                document.getElementById('manage-one').addEventListener('click', () => status.textContent = 'manage-1');
                document.getElementById('manage-two').addEventListener('click', () => status.textContent = 'manage-2');
                document.getElementById('hidden-upload').addEventListener('change', (event) => {
                  status.textContent = 'files:' + event.target.files.length;
                });
                document.getElementById('upload').addEventListener('change', (event) => {
                  status.textContent = 'files:' + event.target.files.length;
                });
              </script>
            </body>
            </html>
            """,
            System.Text.Encoding.UTF8);
        return path;
    }

    private static void WriteJson(ZipArchive archive, string entryName, object payload)
    {
        using var stream = archive.CreateEntry(entryName).Open();
        using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        writer.Flush();
    }

    private static void WriteText(ZipArchive archive, string entryName, string content)
    {
        using var stream = archive.CreateEntry(entryName).Open();
        using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(content);
        writer.Flush();
    }
}
