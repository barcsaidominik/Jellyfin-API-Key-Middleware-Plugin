using Jellyfin.Plugin.ApiKeyMiddleware.Configuration;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Tests;

public sealed class RulesBackupTests : IDisposable
{
    private const string Rules = "[homepage] enforce\nGET /Sessions\n[*] enforce";

    private readonly string _dir = Directory.CreateTempSubdirectory("akm-backup-").FullName;

    private RulesBackup Backup => new(Path.Combine(_dir, "plugin.rules.txt"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ResetConfiguration_RestoresSavedRules()
    {
        Backup.Save(Rules);

        Assert.Equal(Rules, Backup.RulesToRestore(PluginConfiguration.DefaultRules));
        Assert.Equal(Rules, Backup.RulesToRestore("  [*] audit\n"));
    }

    [Fact]
    public void CustomRules_AreNotTouched()
    {
        Backup.Save(Rules);

        Assert.Null(Backup.RulesToRestore("[*] enforce"));
    }

    [Fact]
    public void NoBackup_NothingToRestore()
        => Assert.Null(Backup.RulesToRestore(PluginConfiguration.DefaultRules));

    [Fact]
    public void DefaultSavedOnPurpose_IsNotRestoredOver()
    {
        Backup.Save(Rules);
        Backup.Save(PluginConfiguration.DefaultRules);

        Assert.Null(Backup.RulesToRestore(PluginConfiguration.DefaultRules));
    }

    [Fact]
    public void Save_OverwritesAndLeavesNoTempFile()
    {
        Backup.Save("[a] audit");
        Backup.Save(Rules);

        Assert.Equal(Rules, File.ReadAllText(Backup.Path));
        Assert.Single(Directory.GetFiles(_dir));
    }
}
