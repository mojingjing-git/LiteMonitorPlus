using LiteMonitor;
using LiteMonitor.src.Core.Scenarios;
using Xunit;

namespace LiteMonitor.Tests;

public class ScenarioProfileStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly ScenarioProfileStore _store;

    public ScenarioProfileStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LiteMonitor-Scenario-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new ScenarioProfileStore(Path.Combine(_dir, "profiles.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Create_Update_Conflict_And_Delete()
    {
        var profile = ScenarioProfile.Create("工作", new Settings { PanelWidth = 300 });
        var created = _store.Save(profile, 0);
        Assert.True(created.Success);
        Assert.NotNull(created.Profile);
        Assert.Equal(1, created.Profile!.DefinitionRevision);
        Assert.Single(_store.LoadAll());

        var updated = created.Profile.Clone();
        updated.Name = "工作 2";
        var saved = _store.Save(updated, 1);
        Assert.True(saved.Success);
        Assert.Equal(2, saved.Profile!.DefinitionRevision);
        Assert.Equal("工作 2", _store.Get(created.Profile.Id)!.Name);

        var stale = saved.Profile.Clone();
        stale.Name = "stale";
        var conflict = _store.Save(stale, 1);
        Assert.False(conflict.Success);
        Assert.Equal("revision-conflict", conflict.ErrorCode);
        Assert.Equal("工作 2", _store.Get(created.Profile.Id)!.Name);

        var deleted = _store.Delete(created.Profile.Id, 2);
        Assert.True(deleted.Success);
        Assert.Empty(_store.LoadAll());
    }

    [Fact]
    public void Library_EnforcesProfileCountLimit()
    {
        for (var i = 0; i < ScenarioFieldCatalog.MaxProfiles; i++)
        {
            var result = _store.Save(ScenarioProfile.Create("S" + i, new Settings()), 0);
            Assert.True(result.Success, result.Error);
        }

        var tooMany = _store.Save(ScenarioProfile.Create("S-extra", new Settings()), 0);
        Assert.False(tooMany.Success);
        Assert.Equal("too-many-profiles", tooMany.ErrorCode);
        Assert.Equal(ScenarioFieldCatalog.MaxProfiles, _store.LoadAll().Count);
    }

    [Fact]
    public void CorruptPrimary_FallsBackToBackup()
    {
        Assert.True(_store.Save(ScenarioProfile.Create("A", new Settings()), 0).Success);
        Assert.True(_store.Save(ScenarioProfile.Create("B", new Settings()), 0).Success);

        File.WriteAllText(_store.FilePath, "{ broken json");
        var loaded = _store.LoadAll();

        // 备份保存的是上一次成功提交（A），损坏的当前库（A+B）不会覆盖备份。
        Assert.Single(loaded);
        Assert.NotNull(_store.LastLoadWarning);
        Assert.NotNull(_store.LastQuarantinePath);
        Assert.True(File.Exists(_store.LastQuarantinePath!));
    }

    [Fact]
    public void OversizedLibrary_IsRejectedAndQuarantined()
    {
        File.WriteAllText(_store.FilePath, new string('x', ScenarioFieldCatalog.MaxFileBytes + 1));
        var loaded = _store.LoadAll();

        Assert.Empty(loaded);
        Assert.NotNull(_store.LastLoadWarning);
        Assert.NotNull(_store.LastQuarantinePath);
    }

    [Fact]
    public void UnsupportedSchema_IsReadOnly_NotQuarantined()
    {
        var profile = ScenarioProfile.Create("future", new Settings());
        string json = System.Text.Json.JsonSerializer.Serialize(
            new[] { profile }, ScenarioProfileJson.Options);
        json = json.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 99");
        File.WriteAllText(_store.FilePath, json);

        var loaded = _store.LoadAll();
        Assert.Empty(loaded);
        Assert.True(_store.IsReadOnly);
        Assert.Null(_store.LastQuarantinePath);
        Assert.True(File.Exists(_store.FilePath));
        Assert.Contains("未知高版本", _store.LastLoadWarning);

        var save = _store.Save(ScenarioProfile.Create("new", new Settings()), 0);
        Assert.False(save.Success);
        Assert.Equal("read-only", save.ErrorCode);
        Assert.Equal(json, File.ReadAllText(_store.FilePath));
    }

    [Fact]
    public void UnknownNewField_IsPreservedThroughRoundTrip()
    {
        var profile = ScenarioProfile.Create("A", new Settings());
        profile.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["futureField"] = System.Text.Json.JsonSerializer.SerializeToElement("keep-me")
        };
        var saved = _store.Save(profile, 0);
        Assert.True(saved.Success);

        var loaded = _store.Get(saved.Profile!.Id)!;
        Assert.True(loaded.ExtensionData!.ContainsKey("futureField"));
        Assert.Equal("keep-me", loaded.ExtensionData["futureField"].GetString());
    }
}
