using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace LiteMonitor.AppearanceWindowShapeProbe
{
    /// <summary>单个探针用例的结果。</summary>
    internal sealed class ProbeCase
    {
        public string Name { get; init; } = "";
        public bool Passed { get; private set; } = true;
        public List<string> Failures { get; } = new();
        public JsonObject Details { get; } = new();

        public void Fail(string reason)
        {
            Passed = false;
            Failures.Add(reason);
        }

        public void Require(bool condition, string reason)
        {
            if (!condition) Fail(reason);
        }

        public void Detail(string key, JsonNode? value) => Details[key] = value;
    }
}
