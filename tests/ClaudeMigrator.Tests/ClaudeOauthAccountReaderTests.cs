using ClaudeMigrator.Core.Local;
using ClaudeMigrator.Tests.TestSupport;

namespace ClaudeMigrator.Tests;

public sealed class ClaudeOauthAccountReaderTests
{
    [Fact]
    public void ReadsCurrentOauthAccountFromClaudeJson()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, ".claude.json");
        File.WriteAllText(path, """
        {
          "oauthAccount": {
            "accountUuid": "118701b6-cb3e-4953-bf39-9546781751b8",
            "emailAddress": "plbyrd@gmail.com",
            "displayName": "Payton",
            "organizationUuid": "dc52499b-5e9e-4149-a90d-f6fe5c165c7b",
            "organizationName": "plbyrd@gmail.com's Organization"
          }
        }
        """);

        var account = new ClaudeOauthAccountReader().ReadCurrent(path);

        Assert.NotNull(account);
        Assert.Equal("118701b6-cb3e-4953-bf39-9546781751b8", account!.AccountUuid);
        Assert.Equal("plbyrd@gmail.com", account.EmailAddress);
        Assert.Equal("dc52499b-5e9e-4149-a90d-f6fe5c165c7b", account.OrganizationUuid);
    }

    [Fact]
    public void ReturnsNullWhenClaudeJsonMissing()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, ".claude.json");

        Assert.Null(new ClaudeOauthAccountReader().ReadCurrent(path));
    }

    [Fact]
    public void ReadsDistinctAccountsFromBackups()
    {
        using var workspace = new TestWorkspace();
        var backups = Path.Combine(workspace.Root, "backups");
        Directory.CreateDirectory(backups);

        File.WriteAllText(Path.Combine(backups, ".claude.json.backup.1000"), """
        {"oauthAccount":{"accountUuid":"8e003dee-a2c8-4173-a458-d6a77819ebbb","emailAddress":"ninja@thesharp.ninja","displayName":"Sharp Ninja","organizationUuid":"ff532a36-c1b0-428f-9164-e7c383dfd3da","organizationName":"ninja@thesharp.ninja's Organization"}}
        """);
        File.WriteAllText(Path.Combine(backups, ".claude.json.backup.2000"), """
        {"oauthAccount":{"accountUuid":"8e003dee-a2c8-4173-a458-d6a77819ebbb","emailAddress":"ninja@thesharp.ninja","displayName":"Sharp Ninja","organizationUuid":"ff532a36-c1b0-428f-9164-e7c383dfd3da","organizationName":"ninja@thesharp.ninja's Organization"}}
        """);
        File.WriteAllText(Path.Combine(backups, ".claude.json.backup.3000"), """
        {"oauthAccount":{"accountUuid":"118701b6-cb3e-4953-bf39-9546781751b8","emailAddress":"plbyrd@gmail.com","displayName":"Payton","organizationUuid":"dc52499b-5e9e-4149-a90d-f6fe5c165c7b","organizationName":"plbyrd@gmail.com's Organization"}}
        """);

        var accounts = new ClaudeOauthAccountReader().ReadFromBackups(backups);

        Assert.Equal(2, accounts.Count);
        Assert.Contains(accounts, account => account.AccountUuid == "8e003dee-a2c8-4173-a458-d6a77819ebbb");
        Assert.Contains(accounts, account => account.AccountUuid == "118701b6-cb3e-4953-bf39-9546781751b8");
    }

    [Fact]
    public void IgnoresFilesWithoutOauthAccount()
    {
        using var workspace = new TestWorkspace();
        var backups = Path.Combine(workspace.Root, "backups");
        Directory.CreateDirectory(backups);

        File.WriteAllText(Path.Combine(backups, ".claude.json.backup.1"), "{\"projects\":{}}");

        Assert.Empty(new ClaudeOauthAccountReader().ReadFromBackups(backups));
    }
}
