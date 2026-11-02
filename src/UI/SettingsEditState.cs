using System.Text.Json;
using System.Text.Json.Nodes;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI;

// In-memory only: snapshots can contain plugin credentials and must never be logged or written.
internal sealed class SettingsEditState
{
    private JsonObject _baseline;
    private static readonly string[] RuntimeFields =
    {
        "LastAutoNetwork", "LastAutoDisk", "ScreenDevice", "MaxLimitTipShown",
        "TotalUpload", "TotalDownload", "SessionUploadBytes", "SessionDownloadBytes",
        "LastAutoSaveTime", "LastAlertTime"
    };

    public SettingsEditState(Settings settings) => _baseline = Capture(settings);
    public void Accept(Settings settings) => _baseline = Capture(settings);
    public void AcceptField(Settings settings, string field) => _baseline[field] = Capture(settings)[field]?.DeepClone();
    public bool HasChanges(Settings draft) => !JsonNode.DeepEquals(_baseline, Capture(draft));

    internal static bool EnsureInteractiveEntry(Settings candidate)
    {
        bool blocked = (candidate.HideMainForm || candidate.ClickThrough) &&
            (!candidate.ShowTaskbar || candidate.TaskbarClickThrough) && candidate.HideTrayIcon;
        if (blocked) candidate.HideTrayIcon = false;
        return blocked;
    }

    private static JsonObject Capture(Settings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, SettingsJsonCloning.MemoryCloneOptions)!.AsObject();
        foreach (string field in RuntimeFields) node.Remove(field);
        // Sort numbers are implementation details; compare their relative ordering.
        if (node["MonitorItems"] is JsonArray items)
        {
            foreach (string order in new[] { "SortIndex", "TaskbarSortIndex" })
            {
                int rank = 0;
                foreach (var item in items.OfType<JsonObject>().OrderBy(x => x[order]?.GetValue<int>() ?? 0))
                    item[order] = rank++;
            }
        }
        return node;
    }

    public Settings CreateCandidate(Settings live, Settings draft)
    {
        var current = Capture(draft);
        var result = JsonSerializer.SerializeToNode(live, SettingsJsonCloning.MemoryCloneOptions)!.AsObject();
        foreach (var field in current)
        {
            if (JsonNode.DeepEquals(_baseline[field.Key], field.Value)) continue;
            if (field.Key == "MonitorItems" && field.Value is JsonArray edits && result[field.Key] is JsonArray targets)
            {
                var originals = (_baseline[field.Key] as JsonArray)?.OfType<JsonObject>()
                    .ToDictionary(x => x["Key"]!.GetValue<string>()) ?? new();
                foreach (var edit in edits.OfType<JsonObject>())
                {
                    string key = edit["Key"]!.GetValue<string>();
                    var target = targets.OfType<JsonObject>().FirstOrDefault(x => x["Key"]?.GetValue<string>() == key);
                    if (target == null || !originals.TryGetValue(key, out var original)) continue;
                    foreach (var value in edit)
                        if (value.Key is not "SortIndex" and not "TaskbarSortIndex" && !JsonNode.DeepEquals(original[value.Key], value.Value)) target[value.Key] = value.Value?.DeepClone();
                }
                foreach (string order in new[] { "SortIndex", "TaskbarSortIndex" })
                {
                    string Key(JsonObject item) => item["Key"]!.GetValue<string>();
                    var editedOrder = edits.OfType<JsonObject>().OrderBy(x => x[order]?.GetValue<int>() ?? 0).Select(Key).ToList();
                    var originalOrder = originals.Values.OrderBy(x => x[order]?.GetValue<int>() ?? 0).Select(Key);
                    if (editedOrder.SequenceEqual(originalOrder)) continue;
                    var liveOrder = targets.OfType<JsonObject>().OrderBy(x => x[order]?.GetValue<int>() ?? 0).ToList();
                    var known = originals.Keys.Intersect(editedOrder).ToHashSet();
                    var reordered = editedOrder.Where(known.Contains)
                        .Select(k => liveOrder.FirstOrDefault(x => Key(x) == k)).OfType<JsonObject>().ToArray();
                    int index = 0;
                    for (int slot = 0; slot < liveOrder.Count; slot++)
                        if (known.Contains(Key(liveOrder[slot]))) liveOrder[slot] = reordered[index++];
                    for (int rank = 0; rank < liveOrder.Count; rank++) liveOrder[rank][order] = rank;
                }
            }
            else result[field.Key] = field.Value?.DeepClone();
        }
        return result.Deserialize<Settings>(SettingsJsonCloning.MemoryCloneOptions)!;
    }
}
