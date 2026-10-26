using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace LiteMonitor.src.Plugins
{
    internal static class PluginRuntimeSnapshot
    {
        internal static PluginInstanceConfig Copy(PluginInstanceConfig source) => new()
        {
            Id = source.Id,
            TemplateId = source.TemplateId,
            Enabled = source.Enabled,
            CustomInterval = source.CustomInterval,
            InputValues = new Dictionary<string, string>(source.InputValues),
            Targets = source.Targets?.Select(t => new Dictionary<string, string>(t)).ToList()
                ?? new List<Dictionary<string, string>>()
        };

        internal static PluginTemplate Copy(PluginTemplate source)
        {
            // 往返序列化（同一个类型 + 同一套转换器）实际不可能失败；真失败说明序列化管线坏了，
            // 此时抛明确异常，优于把 null 传给调用方（那会在更远处炸成难定位的 NRE）。
            string json = JsonSerializer.Serialize(source);
            PluginTemplate? copy = JsonSerializer.Deserialize<PluginTemplate>(json);
            return copy ?? throw new InvalidOperationException("PluginTemplate 往返序列化失败：反序列化结果为 null。");
        }

        internal static string Fingerprint(PluginInstanceConfig instance, PluginTemplate template)
        {
            // Avoid the settings secret converters (randomized encryption is not a semantic
            // fingerprint). Dictionary order is immaterial; step/target/output order is not.
            var document = JsonSerializer.SerializeToElement(new
            {
                instance.Id, instance.TemplateId, instance.Enabled, instance.CustomInterval,
                Inputs = instance.InputValues, instance.Targets, Template = template
            });
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes)) WriteCanonical(writer, document);
            return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var value in element.EnumerateArray()) WriteCanonical(writer, value);
                writer.WriteEndArray();
            }
            else element.WriteTo(writer);
        }
    }
}
