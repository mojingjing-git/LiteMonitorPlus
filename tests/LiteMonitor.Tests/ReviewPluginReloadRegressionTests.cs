using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.SystemServices.InfoService;
using Xunit;

namespace LiteMonitor.Tests;

[Collection("cfgfile")]
public class ReviewPluginReloadRegressionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task LoadReconcilesTemplateAndInstanceContentWithoutStartingOrRevivingStoppedManager()
    {
        using var scope = new ReloadScope();
        scope.Load();
        Assert.Empty(scope.Timers);
        scope.Manager.Start();
        var old = Assert.Single(scope.Timers);
        await old.ForceFireAsync().WaitAsync(Limit);
        Assert.Equal("old", InfoService.Instance.GetValue(scope.Id + ".value"));

        // Same version, changed URL: market Load itself must replace the runtime.
        scope.Template.Execution.Steps[0].Url = "http://review.test/new";
        scope.WriteTemplate();
        scope.Load();
        Assert.True(old.Disposed);
        Assert.Equal(2, scope.Timers.Count);
        var current = scope.Timers.Last();
        await current.ForceFireAsync().WaitAsync(Limit);
        Assert.Equal("new", InfoService.Instance.GetValue(scope.Id + ".value"));
        int requests = scope.Transport.Count;
        await old.ForceFireAsync().WaitAsync(Limit); // Deliberately ignores disposal.
        Assert.Equal(requests, scope.Transport.Count);
        Assert.Equal(1, scope.Manager.RegisteredScheduleCount);

        scope.Load();
        scope.Manager.Reload(scope.Settings);
        Assert.Equal(2, scope.Timers.Count);
        scope.Instance.InputValues = scope.Instance.InputValues.Reverse().ToDictionary(p => p.Key, p => p.Value);
        scope.Manager.Reload(scope.Settings);
        Assert.Equal(2, scope.Timers.Count); // Dictionary order is not a content change.

        scope.Instance.InputValues["label"] = "instance changed";
        scope.Manager.Reload(scope.Settings);
        Assert.Equal(3, scope.Timers.Count);
        await scope.Timers.Last().ForceFireAsync().WaitAsync(Limit);
        Assert.Equal("instance changed", InfoService.Instance.GetValue("PROP.Label.DASH." + scope.Id + ".value"));

        scope.Template.Outputs[0].Format = "changed {{v}}";
        scope.WriteTemplate();
        scope.Load();
        Assert.Equal(4, scope.Timers.Count); // Same URL/version, changed output semantics.
        await scope.Timers.Last().ForceFireAsync().WaitAsync(Limit);
        Assert.Equal("changed new", InfoService.Instance.GetValue(scope.Id + ".value"));

        var disabled = new PluginTemplate { Id = "review.disabled" };
        File.WriteAllText(Path.Combine(scope.DirectoryPath, "disabled.json"), JsonSerializer.Serialize(disabled));
        scope.Load();
        Assert.False(Assert.Single(scope.Settings.PluginInstances.Where(i => i.TemplateId == disabled.Id)).Enabled);
        Assert.Equal(4, scope.Timers.Count);

        scope.Manager.Stop();
        scope.Load();
        scope.Manager.Reload(scope.Settings);
        scope.Manager.RestartInstance(scope.Id);
        Assert.Equal(0, scope.Manager.RegisteredScheduleCount);
        Assert.Equal(4, scope.Timers.Count);
        await current.ForceFireAsync().WaitAsync(Limit);
        Assert.Equal(requests + 2, scope.Transport.Count);
    }

    [Fact]
    public async Task UninstallRetiresEvenAlwaysVisibleOldCallbackAndReinstallRemainsDisabled()
    {
        using var scope = new ReloadScope();
        scope.Load();
        scope.Manager.Start();
        var old = Assert.Single(scope.Timers);
        await old.ForceFireAsync().WaitAsync(Limit);
        File.Delete(scope.TemplatePath);
        scope.Load();
        Assert.Equal(0, scope.Manager.RegisteredScheduleCount);
        Assert.Empty(scope.Settings.PluginInstances);
        Assert.Equal("", InfoService.Instance.GetValue(scope.Id + ".value"));
        int requests = scope.Transport.Count;
        await old.ForceFireAsync().WaitAsync(Limit);
        Assert.Equal(requests, scope.Transport.Count);
        scope.WriteTemplate();
        scope.Load();
        Assert.False(Assert.Single(scope.Settings.PluginInstances).Enabled);
        Assert.Equal(0, scope.Manager.RegisteredScheduleCount);
    }

    [Theory]
    [InlineData("outputs", false)]
    [InlineData("error", true)]
    [InlineData("cache-write", false)]
    [InlineData("api_text", false)]
    public async Task RetiredPreparedCompletionCannotPublishAnyResultPropertiesKeysOrCache(string kind, bool fail)
    {
        using var scope = new ReloadScope();
        if (kind == "api_text")
        {
            scope.Template.Execution.Type = "api_text";
            scope.Template.Execution.Url = "http://review.test/old";
        }
        scope.Transport.Respond = (request, _) => Task.FromResult(
            fail && request.RequestUri.AbsolutePath == "/old"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json(request.RequestUri.AbsolutePath.Trim('/')));
        scope.WriteTemplate();
        scope.Load();
        scope.Manager.Start();
        var old = Assert.Single(scope.Timers);
        var oldInstance = RuntimeInstance(scope.Manager, scope.Id);
        var barrier = new CommitBarrier();
        scope.Manager.ExecutorInternal.BeforePublicationAsync = (id, stage) =>
            id == scope.Id && stage == kind ? barrier.BlockOnceAsync() : Task.CompletedTask;
        Task pending = old.ForceFireAsync();
        try
        {
            await barrier.Entered.Task.WaitAsync(Limit);
            scope.Template.Execution.Url = "http://review.test/new";
            scope.Template.Execution.Steps[0].Url = "http://review.test/new";
            scope.Instance.InputValues = Inputs("new");
            scope.WriteTemplate();
            scope.Load();
            await scope.Timers.Last().ForceFireAsync().WaitAsync(Limit);
            var values = Values(scope.Id);
            var cache = CacheSnapshot(scope.Manager.ExecutorInternal);
            Assert.Empty(oldInstance.KeyCache);
            if (kind != "api_text")
            {
                Assert.Equal("new", values[scope.Id + ".value"]);
                Assert.Equal("new", values["PROP.Label.DASH." + scope.Id + ".value"]);
                Assert.Equal("new-unit", values[scope.Id + ".value.Unit"]);
                Assert.NotEmpty(RuntimeInstance(scope.Manager, scope.Id).KeyCache);
                Assert.NotEmpty(cache);
            }
            barrier.Release.TrySetResult();
            await pending.WaitAsync(Limit);
            Assert.Equal(values.OrderBy(p => p.Key), Values(scope.Id).OrderBy(p => p.Key));
            Assert.Equal(cache, CacheSnapshot(scope.Manager.ExecutorInternal));
            Assert.Empty(oldInstance.KeyCache);
            Assert.Equal(0, scope.Manager.GetConsecutiveFailures(scope.Id));
            Assert.Equal(1, old.Starts);
        }
        finally
        {
            scope.Manager.ExecutorInternal.BeforePublicationAsync = null;
            barrier.Release.TrySetResult();
            await pending.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task RetiredExpiredCacheReaderCannotDeleteReplacementEntry()
    {
        using var scope = new ReloadScope();
        scope.Template.Execution.Steps[0].CacheMinutes = 1;
        scope.WriteTemplate();
        scope.Load();
        scope.Manager.Start();
        var old = Assert.Single(scope.Timers);
        await old.ForceFireAsync().WaitAsync(Limit);
        foreach (DictionaryEntry entry in Cache(scope.Manager.ExecutorInternal))
            entry.Value.GetType().GetProperty("Timestamp").SetValue(entry.Value, DateTime.MinValue);
        var barrier = new CommitBarrier();
        scope.Manager.ExecutorInternal.BeforePublicationAsync = (_, kind) =>
            kind == "cache-read" ? barrier.BlockOnceAsync() : Task.CompletedTask;
        Task pending = old.ForceFireAsync();
        try
        {
            await barrier.Entered.Task.WaitAsync(Limit);
            scope.Instance.InputValues = Inputs("new"); // Same request/cache key, different runtime.
            scope.Manager.Reload(scope.Settings);
            await scope.Timers.Last().ForceFireAsync().WaitAsync(Limit);
            var cache = CacheSnapshot(scope.Manager.ExecutorInternal);
            int requests = scope.Transport.Count;
            barrier.Release.TrySetResult();
            await pending.WaitAsync(Limit);
            Assert.Equal(cache, CacheSnapshot(scope.Manager.ExecutorInternal));
            await scope.Timers.Last().ForceFireAsync().WaitAsync(Limit);
            Assert.Equal(requests, scope.Transport.Count); // The new entry was not expired/deleted by old work.
        }
        finally
        {
            scope.Manager.ExecutorInternal.BeforePublicationAsync = null;
            barrier.Release.TrySetResult();
            await pending.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task RetiredCacheWriterCannotEvictOtherNewEntries()
    {
        using var scope = new ReloadScope();
        scope.Load();
        scope.Manager.Start();
        var barrier = new CommitBarrier();
        scope.Manager.ExecutorInternal.BeforePublicationAsync = (id, kind) =>
            id == scope.Id && kind == "cache-write" ? barrier.BlockOnceAsync() : Task.CompletedTask;
        Task pending = scope.Timers.Single().ForceFireAsync();
        try
        {
            await barrier.Entered.Task.WaitAsync(Limit);
            scope.Instance.InputValues = Inputs("new");
            scope.Manager.Reload(scope.Settings);
            await scope.Timers.Last().ForceFireAsync().WaitAsync(Limit);
            // Populate via the production step path so an admitted old cache writer
            // would take the >100 eviction branch, rather than just add its own key.
            for (int i = 0; i < 100; i++)
                await RunStepAsync(scope.Manager.ExecutorInternal, new PluginInstanceConfig { Id = "filler" },
                    Step("http://review.test/fill/" + i)).WaitAsync(Limit);
            Assert.Equal(101, Cache(scope.Manager.ExecutorInternal).Count);
            var cache = CacheSnapshot(scope.Manager.ExecutorInternal);
            barrier.Release.TrySetResult();
            await pending.WaitAsync(Limit);
            Assert.Equal(cache, CacheSnapshot(scope.Manager.ExecutorInternal));
        }
        finally
        {
            scope.Manager.ExecutorInternal.BeforePublicationAsync = null;
            barrier.Release.TrySetResult();
            await pending.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task RetiredThirdFailureCannotResetClientsClearNewCacheChangeFailureCountOrRearm()
    {
        using var scope = new ReloadScope();
        scope.Transport.Respond = (request, _) => Task.FromResult(request.RequestUri.AbsolutePath == "/old"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Json("new"));
        scope.Load();
        scope.Manager.Start();
        var old = Assert.Single(scope.Timers);
        await old.ForceFireAsync().WaitAsync(Limit);
        await old.ForceFireAsync().WaitAsync(Limit);
        Assert.Equal(2, scope.Manager.GetConsecutiveFailures(scope.Id));
        var barrier = new CommitBarrier();
        scope.Manager.BeforeScheduleCommitAsync = _ => barrier.BlockOnceAsync();
        Task pending = old.ForceFireAsync();
        try
        {
            await barrier.Entered.Task.WaitAsync(Limit);
            scope.Template.Execution.Steps[0].Url = "http://review.test/new";
            scope.WriteTemplate();
            scope.Load();
            await scope.Timers.Last().ForceFireAsync().WaitAsync(Limit);
            var cache = CacheSnapshot(scope.Manager.ExecutorInternal);
            var http = Field(scope.Manager.ExecutorInternal, "_http");
            int starts = old.Starts;
            barrier.Release.TrySetResult();
            await pending.WaitAsync(Limit);
            Assert.Equal(0, scope.Manager.GetConsecutiveFailures(scope.Id));
            Assert.Equal(0, scope.Resets);
            Assert.Same(http, Field(scope.Manager.ExecutorInternal, "_http"));
            Assert.Equal(cache, CacheSnapshot(scope.Manager.ExecutorInternal));
            Assert.Equal(starts, old.Starts);
            Assert.Equal("new", InfoService.Instance.GetValue(scope.Id + ".value"));
        }
        finally
        {
            scope.Manager.BeforeScheduleCommitAsync = null;
            barrier.Release.TrySetResult();
            await pending.WaitAsync(Limit);
        }
    }

    [Theory]
    [InlineData("B", false)]
    [InlineData(".B", false)]
    [InlineData("B", true)]
    [InlineData(".B", true)]
    public async Task ParentHotReloadAndRemovalOnlyTouchOwnedCompleteKeysWhileNeighborStaysUnchanged(
        string neighborSuffix, bool uninstallTemplate)
    {
        using var scope = new ReloadScope();
        // Match production's template-id -> default instance-id behavior. Both ids
        // are valid templates; the neighbor has its own unchanged template/runtime.
        scope.Instance.TemplateId = scope.Id;
        scope.Template.Id = scope.Id;
        scope.WriteTemplate();
        var neighborTemplate = PluginRuntimeSnapshot.Copy(scope.Template);
        neighborTemplate.Id = scope.Id + neighborSuffix;
        File.WriteAllText(Path.Combine(scope.DirectoryPath, "neighbor.json"), JsonSerializer.Serialize(neighborTemplate));
        var neighbor = new PluginInstanceConfig
        {
            Id = neighborTemplate.Id, TemplateId = neighborTemplate.Id, Enabled = true, InputValues = Inputs("neighbor")
        };
        scope.Settings.PluginInstances.Add(neighbor);
        scope.Load();
        scope.Manager.VisibilityProbe = null; // Exercise the real monitor ownership/visibility path.
        scope.Manager.Start();
        foreach (var timer in scope.Timers.ToList()) await timer.ForceFireAsync().WaitAsync(Limit);

        var neighborRuntime = RuntimeInstance(scope.Manager, neighbor.Id);
        var neighborTimer = (ForceTimer)((IDictionary)Field(scope.Manager, "_timers"))[neighbor.Id];
        string monitorKey = "DASH." + neighbor.Id + ".value";
        var neighborItem = Assert.Single(scope.Settings.MonitorItems.Where(i => i.Key == monitorKey));
        neighborItem.UnitPanel = "neighbor-panel";
        neighborItem.UnitTaskbar = "neighbor-taskbar";
        neighborItem.SortIndex = 5;
        neighborItem.TaskbarSortIndex = 7;
        string itemState = JsonSerializer.Serialize(neighborItem);
        var neighborValues = Values(neighbor.Id);
        var neighborKeys = neighborValues.Keys.ToArray();
        var neighborCache = CacheSnapshot(scope.Manager.ExecutorInternal)
            .Where(key => key.StartsWith(neighbor.Id + "|", StringComparison.Ordinal)).ToArray();
        var neighborKeyCache = neighborRuntime.KeyCache.ToArray();
        int neighborStarts = neighborTimer.Starts;
        Assert.NotEmpty(neighborCache);
        Assert.NotEmpty(neighborKeyCache);

        void AssertNeighborUnchanged()
        {
            Assert.Same(neighborRuntime, RuntimeInstance(scope.Manager, neighbor.Id));
            Assert.Same(neighborTimer, ((IDictionary)Field(scope.Manager, "_timers"))[neighbor.Id]);
            Assert.False(neighborTimer.Disposed);
            Assert.Equal(neighborStarts, neighborTimer.Starts);
            Assert.Same(neighborItem, Assert.Single(scope.Settings.MonitorItems.Where(i => i.Key == monitorKey)));
            Assert.Equal(itemState, JsonSerializer.Serialize(neighborItem));
            Assert.Equal("neighbor", scope.Manager.TryGetSmartLabel(monitorKey));
            Assert.Equal(neighborValues.OrderBy(p => p.Key),
                neighborKeys.ToDictionary(key => key, key => InfoService.Instance.GetValue(key)).OrderBy(p => p.Key));
            Assert.Equal(neighborCache, CacheSnapshot(scope.Manager.ExecutorInternal)
                .Where(key => key.StartsWith(neighbor.Id + "|", StringComparison.Ordinal)).ToArray());
            Assert.Equal(neighborKeyCache.Length, neighborRuntime.KeyCache.Count);
            foreach (var pair in neighborKeyCache) Assert.Same(pair.Value, neighborRuntime.KeyCache[pair.Key]);
        }

        var originalParentKeys = Values(scope.Id).Keys.ToArray();
        var firstParentTimer = (ForceTimer)((IDictionary)Field(scope.Manager, "_timers"))[scope.Id];
        scope.Template.Execution.Steps[0].Url = "http://review.test/new";
        scope.WriteTemplate();
        scope.Load(); // Same version, URL change: A retires; A.B must not need any repair tick.
        Assert.Equal(3, scope.Timers.Count);
        AssertNeighborUnchanged();
        int requests = scope.Transport.Count;
        await firstParentTimer.ForceFireAsync().WaitAsync(Limit);
        Assert.Equal(requests, scope.Transport.Count);
        AssertNeighborUnchanged();
        await scope.Timers.Last().ForceFireAsync().WaitAsync(Limit);
        Assert.Equal("new", InfoService.Instance.GetValue(scope.Id + ".value"));
        AssertNeighborUnchanged();

        // Also exercise orphan removal and insertion. Neither may delete/reindex a
        // neighbor item or clear a neighbor's label/unit/color/value.
        scope.Template.Outputs[0].Key = "replacement";
        scope.WriteTemplate();
        scope.Load();
        Assert.Equal(4, scope.Timers.Count);
        Assert.DoesNotContain(scope.Settings.MonitorItems, item => item.Key == "DASH." + scope.Id + ".value");
        Assert.Contains(scope.Settings.MonitorItems, item => item.Key == "DASH." + scope.Id + ".replacement");
        Assert.All(originalParentKeys, key => Assert.Equal("", InfoService.Instance.GetValue(key)));
        AssertNeighborUnchanged();
        await scope.Timers.Last().ForceFireAsync().WaitAsync(Limit);
        Assert.Equal("new", InfoService.Instance.GetValue(scope.Id + ".replacement"));
        AssertNeighborUnchanged();

        var currentParentKeys = Values(scope.Id, "replacement").Keys.ToArray();
        if (uninstallTemplate)
        {
            File.Delete(scope.TemplatePath);
            scope.Load();
        }
        else
        {
            // The ownership rule must still work after A has left the live settings.
            scope.Settings.PluginInstances.Remove(scope.Instance);
            scope.Manager.RemoveInstance(scope.Id);
        }
        Assert.DoesNotContain(scope.Settings.PluginInstances, instance => instance.Id == scope.Id);
        Assert.DoesNotContain(scope.Settings.MonitorItems, item => item.Key == "DASH." + scope.Id + ".replacement");
        Assert.All(currentParentKeys.Concat(originalParentKeys), key => Assert.Equal("", InfoService.Instance.GetValue(key)));
        Assert.Equal(1, scope.Manager.RegisteredScheduleCount);
        Assert.Equal(neighborCache, CacheSnapshot(scope.Manager.ExecutorInternal));
        AssertNeighborUnchanged();

        requests = scope.Transport.Count;
        foreach (var timer in scope.Timers.Where(timer => !ReferenceEquals(timer, neighborTimer)))
            await timer.ForceFireAsync().WaitAsync(Limit);
        Assert.Equal(requests, scope.Transport.Count);
        AssertNeighborUnchanged();
        await neighborTimer.ForceFireAsync().WaitAsync(Limit);
        Assert.Equal(requests + 1, scope.Transport.Count); // Still visible and scheduled after A is removed.
        Assert.Equal("old", InfoService.Instance.GetValue(neighbor.Id + ".value"));
    }

    [Fact]
    public async Task StopDuringTimerFactoryCannotInstallOrArmAnObsoleteReservation()
    {
        using var scope = new ReloadScope();
        scope.Load();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Manager.TimerFactory = (interval, callback) =>
        {
            var timer = scope.NewTimer(interval, callback);
            entered.TrySetResult();
            release.Task.WaitAsync(Limit).GetAwaiter().GetResult();
            return timer;
        };
        Task start = Task.Run(scope.Manager.Start);
        try
        {
            await entered.Task.WaitAsync(Limit);
            scope.Manager.Stop();
        }
        finally
        {
            release.TrySetResult();
            await start.WaitAsync(Limit);
        }
        Assert.Equal(0, scope.Manager.RegisteredScheduleCount);
        Assert.Equal(0, Assert.Single(scope.Timers).Starts);
        Assert.True(scope.Timers.Single().Disposed);
    }

    [Fact]
    public async Task RetiringOneWaiterLeavesSharedHttpAliveForAnotherWaiter()
    {
        using var scope = new ReloadScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken transportToken = default;
        scope.Transport.Respond = async (_, token) =>
        {
            transportToken = token;
            entered.TrySetResult();
            await release.Task.WaitAsync(Limit);
            token.ThrowIfCancellationRequested();
            return Json("survivor");
        };
        scope.Manager.ExecutorInternal.BeforePublicationAsync = (id, kind) =>
        {
            if (id == "survivor" && kind == "inflight-wait") joined.TrySetResult();
            return Task.CompletedTask;
        };
        scope.Load();
        scope.Manager.Start();
        Task retired = scope.Timers.Single().ForceFireAsync();
        Task<bool> survivor = null;
        try
        {
            await entered.Task.WaitAsync(Limit);
            survivor = scope.Manager.ExecutorInternal.ExecuteInstanceAsync(new PluginInstanceConfig
            {
                Id = "survivor", Enabled = true, InputValues = Inputs("survivor")
            }, scope.Template);
            await joined.Task.WaitAsync(Limit);
            scope.Manager.StopInstance(scope.Id);
            await retired.WaitAsync(Limit);
            Assert.False(transportToken.IsCancellationRequested);
            release.TrySetResult();
            Assert.True(await survivor.WaitAsync(Limit));
            Assert.Equal(1, scope.Transport.Count);
            Assert.Equal("survivor", InfoService.Instance.GetValue("survivor.value"));
            Assert.Equal(PluginConstants.STATUS_LOADING, InfoService.Instance.GetValue(scope.Id + ".value"));
        }
        finally
        {
            release.TrySetResult();
            await retired.WaitAsync(Limit);
            if (survivor != null) await survivor.WaitAsync(Limit);
            scope.Manager.ExecutorInternal.BeforePublicationAsync = null;
            InfoService.Instance.RemoveDataByPrefix("survivor.");
            InfoService.Instance.RemoveDataByPrefix("PROP.Label.DASH.survivor.");
            InfoService.Instance.RemoveDataByPrefix("PROP.ShortLabel.DASH.survivor.");
        }
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { v = value })) };

    private static Dictionary<string, string> Inputs(string label) => new()
        { ["label"] = label, ["unit"] = label + "-unit", ["color"] = label == "old" ? "1" : "2" };

    private static PluginExecutionStep Step(string url) => new()
        { Id = "fetch", Url = url, CacheMinutes = -1, Extract = new() { ["v"] = "v" } };

    internal static async Task RunStepAsync(PluginExecutor executor, PluginInstanceConfig instance, PluginExecutionStep step)
    {
        var method = typeof(PluginExecutor).GetMethod("ExecuteStepAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await ((Task)method.Invoke(executor, new object[] { instance, step, new Dictionary<string, string>(), "", CancellationToken.None }));
    }

    private static PluginInstanceConfig RuntimeInstance(PluginManager manager, string id)
    {
        var runtime = ((IDictionary)Field(manager, "_runtimes"))[id];
        return (PluginInstanceConfig)runtime.GetType().GetField("Instance", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(runtime);
    }

    internal static object Field(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

    private static IDictionary Cache(PluginExecutor executor) => (IDictionary)Field(executor, "_stepCache");

    private static string[] CacheSnapshot(PluginExecutor executor)
    {
        var cache = Cache(executor);
        return cache.Keys.Cast<string>().Select(key =>
        {
            var value = cache[key];
            return key + "|" + value.GetType().GetProperty("RawResponse").GetValue(value) + "|" +
                ((DateTime)value.GetType().GetProperty("Timestamp").GetValue(value)).Ticks;
        }).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, string> Values(string id, string outputKey = "value") => new[]
        { id, id + "." + outputKey, id + "." + outputKey + ".Color", id + "." + outputKey + ".Unit",
          "DASH." + id + "." + outputKey, "DASH." + id + "." + outputKey + ".Unit",
          "PROP.Label.DASH." + id + "." + outputKey, "PROP.ShortLabel.DASH." + id + "." + outputKey }
        .ToDictionary(k => k, k => InfoService.Instance.GetValue(k));

    private sealed class CommitBarrier
    {
        private int _entered;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task BlockOnceAsync()
        {
            if (Interlocked.Exchange(ref _entered, 1) != 0) return Task.CompletedTask;
            Entered.TrySetResult();
            return Release.Task.WaitAsync(Limit);
        }
    }

    private sealed class ForceTimer : IPluginTimer
    {
        private readonly Func<IPluginTimer, Task> _callback;
        internal ForceTimer(double interval, Func<IPluginTimer, Task> callback) { Interval = interval; _callback = callback; }
        public double Interval { get; set; }
        internal int Starts { get; private set; }
        internal bool Disposed { get; private set; }
        public void Start() { if (Disposed) throw new ObjectDisposedException(nameof(ForceTimer)); Starts++; }
        public void Stop() { }
        public void Dispose() => Disposed = true;
        internal Task ForceFireAsync() => _callback(this);
    }

    private sealed class ReloadScope : IDisposable
    {
        private readonly FieldInfo _settingsField = typeof(Settings).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);
        private readonly object _savedSettings;
        private readonly bool _savedBlock;
        private readonly object _savedBlockOwner;
        private readonly string _savedDirectory = Environment.CurrentDirectory;
        private readonly FieldInfo _blockOwner = typeof(SettingsHelper).GetField("_saveBlockOwner", BindingFlags.NonPublic | BindingFlags.Static);
        internal string Id { get; } = "review-" + Guid.NewGuid().ToString("N");
        internal string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory, "review-plugin-" + Guid.NewGuid().ToString("N"));
        internal string TemplatePath => Path.Combine(DirectoryPath, "template.json");
        internal Settings Settings { get; } = new();
        internal PluginManager Manager { get; }
        internal ReviewPluginTransport Transport { get; } = new();
        internal List<ForceTimer> Timers { get; } = new();
        internal PluginTemplate Template { get; }
        internal PluginInstanceConfig Instance { get; }
        internal int Resets;

        internal ReloadScope()
        {
            _savedSettings = _settingsField.GetValue(null);
            _savedBlock = SettingsHelper.GlobalBlockSave;
            _savedBlockOwner = _blockOwner.GetValue(null);
            try
            {
                SettingsHelper.GlobalBlockSave = true;
                _settingsField.SetValue(null, Settings);
                Directory.CreateDirectory(DirectoryPath);
                Environment.CurrentDirectory = DirectoryPath;
                Instance = new() { Id = Id, TemplateId = "review.template", Enabled = true, InputValues = Inputs("old") };
                Settings.PluginInstances = new() { Instance };
                Settings.MonitorItems = new();
                Template = new()
                {
                    Id = Instance.TemplateId,
                    Execution = new() { Type = "chain", Steps = new() { Step("http://review.test/old") } },
                    Outputs = new() { new PluginOutput { Key = "value", Format = "{{v}}", Label = "{{label}}",
                        ShortLabel = "{{label}}", Color = "{{color}}", Unit = "{{unit}}" } }
                };
                Transport.Respond = (request, _) => Task.FromResult(Json(request.RequestUri.AbsolutePath.Trim('/')));
                Manager = new PluginManager();
                ReviewPluginTransport.Install(Manager.ExecutorInternal, Transport);
                Manager.VisibilityProbe = _ => true;
                Manager.TimerFactory = NewTimer;
                Manager.NetworkResetObserver = _ => Resets++;
                WriteTemplate();
            }
            catch { Dispose(); throw; }
        }

        internal ForceTimer NewTimer(double interval, Func<IPluginTimer, Task> callback)
        {
            var timer = new ForceTimer(interval, callback);
            lock (Timers) Timers.Add(timer);
            return timer;
        }

        internal void Load() => Manager.LoadPlugins(DirectoryPath);
        internal void WriteTemplate() => File.WriteAllText(TemplatePath, JsonSerializer.Serialize(Template));
        public void Dispose()
        {
            try
            {
                if (Manager != null)
                {
                    Manager.ExecutorInternal.BeforePublicationAsync = null;
                    Manager.BeforeScheduleCommitAsync = null;
                    Manager.VisibilityProbe = null;
                    Manager.ExecutionProbe = null;
                    Manager.NetworkResetObserver = null;
                    Manager.Stop();
                    Manager.ExecutorInternal.Dispose();
                }
                if (Manager != null)
                {
                    var owned = (IDictionary)Field(Manager, "_ownedDataKeys");
                    var keys = owned.Values.Cast<HashSet<string>>().SelectMany(set => set).ToHashSet(StringComparer.Ordinal);
                    InfoService.Instance.RemoveDataByKeys(keys);
                }
            }
            finally
            {
                _settingsField.SetValue(null, _savedSettings);
                SettingsHelper.GlobalBlockSave = _savedBlock;
                _blockOwner.SetValue(null, _savedBlockOwner);
                Environment.CurrentDirectory = _savedDirectory;
                string absolute = Path.GetFullPath(DirectoryPath);
                if (absolute.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) && Directory.Exists(absolute))
                    Directory.Delete(absolute, true);
            }
        }
    }
}

internal sealed class ReviewPluginTransport : HttpMessageHandler
{
    private int _count;
    internal int Count => Volatile.Read(ref _count);
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Interlocked.Increment(ref _count);
        return Respond(request, token);
    }
    internal static void Install(PluginExecutor executor, ReviewPluginTransport transport)
    {
        var field = typeof(PluginExecutor).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance);
        ((HttpClient)field.GetValue(executor)).Dispose();
        field.SetValue(executor, new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(8) });
    }
}
