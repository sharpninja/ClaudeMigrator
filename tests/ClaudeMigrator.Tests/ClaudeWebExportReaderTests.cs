using System.IO.Compression;
using System.Text;
using ClaudeMigrator.Core.Web;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class ClaudeWebExportReaderTests
{
    [Fact]
    public void ReaderParsesClaudeWebExportSchema()
    {
        using var workspace = new TestWorkspace();
        var archive = CreateClaudeWebExport(workspace.Root);

        var export = new ClaudeWebExportReader().Read(archive);

        Assert.Equal("source-account-1", export.SourceAccountUuid);
        Assert.Equal("source@example.com", export.SourceAccountEmail);
        Assert.Single(export.Conversations);
        Assert.Equal("chat-1", export.Conversations[0].Uuid);
        Assert.Equal("Migration Test Chat", export.Conversations[0].Name);
        Assert.Equal(2, export.Conversations[0].Messages.Count);
        Assert.Equal("human", export.Conversations[0].Messages[0].Sender);
        Assert.Equal("assistant", export.Conversations[0].Messages[1].Sender);
        Assert.Single(export.Projects);
        Assert.Equal("project-1", export.Projects[0].Uuid);
        Assert.Single(export.Projects[0].Docs);
        Assert.Equal("notes.md", export.Projects[0].Docs[0].FileName);
        Assert.Equal("Global memory text.", export.ConversationsMemory);
        Assert.Equal("Project memory text.", export.ProjectMemories["project-1"]);
    }

    [Fact]
    public void TranscriptRendererPreservesMessageTextAndRawJson()
    {
        using var workspace = new TestWorkspace();
        var export = new ClaudeWebExportReader().Read(CreateClaudeWebExport(workspace.Root));

        var markdown = ClaudeWebTranscriptRenderer.RenderConversation(export.Conversations[0]);

        Assert.Contains("Source conversation UUID: chat-1", markdown);
        Assert.Contains("Hello Claude", markdown);
        Assert.Contains("Hello human", markdown);
        Assert.Contains("Raw source message JSON", markdown);
        Assert.Contains("\"sender\": \"assistant\"", markdown);
    }

    private static string CreateClaudeWebExport(string root)
    {
        var archivePath = Path.Combine(root, "claude-web-export.zip");
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        AddEntry(
            archive,
            "users.json",
            """
            [
              {
                "uuid": "source-account-1",
                "full_name": "Source User",
                "email_address": "source@example.com"
              }
            ]
            """);
        AddEntry(
            archive,
            "memories.json",
            """
            [
              {
                "account_uuid": "source-account-1",
                "conversations_memory": "Global memory text.",
                "project_memories": {
                  "project-1": "Project memory text."
                }
              }
            ]
            """);
        AddEntry(
            archive,
            "conversations.json",
            """
            [
              {
                "uuid": "chat-1",
                "name": "Migration Test Chat",
                "summary": "Test summary",
                "created_at": "2026-05-01T00:00:00Z",
                "updated_at": "2026-05-01T00:01:00Z",
                "account": { "uuid": "source-account-1" },
                "chat_messages": [
                  {
                    "uuid": "msg-1",
                    "sender": "human",
                    "text": "Hello Claude",
                    "content": [{ "type": "text", "text": "Hello Claude" }],
                    "created_at": "2026-05-01T00:00:00Z",
                    "updated_at": "2026-05-01T00:00:00Z",
                    "attachments": [],
                    "files": [],
                    "parent_message_uuid": "00000000-0000-4000-8000-000000000000"
                  },
                  {
                    "uuid": "msg-2",
                    "sender": "assistant",
                    "text": "Hello human",
                    "content": [{ "type": "text", "text": "Hello human" }],
                    "created_at": "2026-05-01T00:00:01Z",
                    "updated_at": "2026-05-01T00:00:01Z",
                    "attachments": [],
                    "files": [],
                    "parent_message_uuid": "msg-1"
                  }
                ]
              }
            ]
            """);
        AddEntry(
            archive,
            "projects/project-1.json",
            """
            {
              "uuid": "project-1",
              "name": "Migration Test Project",
              "description": "Project description",
              "is_private": true,
              "is_starter_project": false,
              "prompt_template": "Project prompt",
              "created_at": "2026-05-01T00:00:00Z",
              "updated_at": "2026-05-01T00:00:00Z",
              "docs": [
                {
                  "uuid": "doc-1",
                  "file_name": "notes.md",
                  "content": "Project notes"
                }
              ]
            }
            """);

        return archivePath;
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }
}
