using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteMonitor.src.Core.Scenarios
{
    /// <summary>
    /// 场景定义。名称仅用于显示；文件定位始终使用固定库路径和 Id。
    /// </summary>
    public sealed class ScenarioProfile
    {
        public int SchemaVersion { get; set; } = ScenarioFieldCatalog.CurrentSchemaVersion;
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public long DefinitionRevision { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public ScenarioSettingsProjection Settings { get; set; } = new();

        /// <summary>未知字段原样保留，但不参与应用；未知高版本整体不应用。</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        [JsonIgnore]
        public bool IsSupportedVersion => SchemaVersion == ScenarioFieldCatalog.CurrentSchemaVersion;

        public static ScenarioProfile Create(string name, Settings source, DateTimeOffset? now = null)
        {
            var timestamp = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
            return new ScenarioProfile
            {
                SchemaVersion = ScenarioFieldCatalog.CurrentSchemaVersion,
                Id = Guid.NewGuid().ToString("N"),
                Name = name ?? "",
                DefinitionRevision = 1,
                CreatedAtUtc = timestamp,
                UpdatedAtUtc = timestamp,
                Settings = ScenarioProjection.Capture(source)
            };
        }

        public ScenarioProfile Clone()
        {
            var json = JsonSerializer.Serialize(this, ScenarioProfileJson.Options);
            return JsonSerializer.Deserialize<ScenarioProfile>(json, ScenarioProfileJson.Options)
                   ?? throw new InvalidOperationException("场景序列化往返失败。");
        }
    }

    public static class ScenarioProfileJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
    }

    public sealed record ScenarioStoreResult(bool Success, string? ErrorCode, string? Error, ScenarioProfile? Profile = null)
    {
        public static ScenarioStoreResult Ok(ScenarioProfile profile) => new(true, null, null, profile);
        public static ScenarioStoreResult Fail(string code, string error) => new(false, code, error);
    }
}
