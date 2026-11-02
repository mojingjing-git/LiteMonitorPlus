using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using LiteMonitor;
using LiteMonitor.Core.Actions.Services;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    public sealed class ThemeStudioSaveOrchestrationTests : IDisposable
    {
        private const string ThemeName = "SaveOrchestration";
        private const string ThemeJson = """
        {
          "version": 3,
          "name": "SaveOrchestration",
          "color": {
            "background": "#18212B",
            "groupBackground": "#222D38",
            "border": "#465868",
            "textPrimary": "#F4F7FA",
            "textSecondary": "#B3C0CC",
            "textTitle": "#67B7FF"
          },
          "layout": {
            "rowHeight": 40,
            "padding": 12
          }
        }
        """;

        private readonly string _root;
        private readonly string _themesDir;
        private readonly string _settingsDir;
        private readonly string? _previousSettingsPath;
        private readonly bool _previousBlock;
        private readonly Action? _previousSaveGate;
        private readonly string _previousThemeDir;

        public ThemeStudioSaveOrchestrationTests()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "litemonitor-save-orchestration-" + Guid.NewGuid().ToString("N"));
            _themesDir = Path.Combine(_root, "themes");
            _settingsDir = Path.Combine(_root, "settings");
            Directory.CreateDirectory(_themesDir);
            Directory.CreateDirectory(_settingsDir);

            _previousSettingsPath = SettingsHelper.PathOverride;
            _previousBlock = SettingsHelper.GlobalBlockSave;
            _previousSaveGate = SettingsHelper.BeforeSaveGateForTests;
            _previousThemeDir = ThemeDocumentStore.ThemeDir;

            SettingsHelper.PathOverride = _settingsDir;
            SettingsHelper.GlobalBlockSave = false;
            SettingsHelper.BeforeSaveGateForTests = null;
            ThemeDocumentStore.SetThemeDirForTests(_themesDir);
            ThemeDocumentStore.SetRecoveryDirForTests(Path.Combine(_root, "drafts"));

            File.WriteAllText(
                Path.Combine(_themesDir, ThemeName + ".json"),
                ThemeJson,
                new UTF8Encoding(false));
        }

        public void Dispose()
        {
            SettingsHelper.BeforeSaveGateForTests = _previousSaveGate;
            SettingsHelper.GlobalBlockSave = _previousBlock;
            SettingsHelper.PathOverride = _previousSettingsPath;
            ThemeDocumentStore.SetThemeDirForTests(
                string.Equals(_previousThemeDir, ThemeManager.ThemeDir, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : _previousThemeDir);
            ThemeDocumentStore.ResetRecoveryDir();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void ApplySession_WhenDocumentSaveIsBlocked_DoesNotInvokeRuntimeGateway()
        {
            RunSta(() =>
            {
                var gateway = new CountingApplyGateway();
                using var form = new ThemeStudioForm(gateway);
                form.OpenThemeForTesting(ThemeName);

                ThemeDocumentSession session = form.SessionForTesting
                    ?? throw new InvalidOperationException("Theme session was not opened.");
                session.ApplyEdit(new ThemeFieldEdit(
                    "color.background",
                    JsonValue.Create("#223344"),
                    theme => theme.Color.Background = "#223344"));

                string path = Path.Combine(_themesDir, ThemeName + ".json");
                byte[] externallyChanged = Encoding.UTF8.GetBytes(
                    ThemeJson.Replace("#18212B", "#334455", StringComparison.Ordinal));
                File.WriteAllBytes(path, externallyChanged);

                form.ApplyWithoutDialogsForTesting();

                Assert.Equal(0, gateway.CallCount);
                Assert.True(session.IsDirty);
                Assert.Equal(externallyChanged, File.ReadAllBytes(path));
                Assert.Contains("保存失败", form.SavedStatusTextForTesting);
            });
        }

        [Theory]
        [InlineData(
            ThemeStudioForm.SettingsRollbackState.Restored,
            "Settings 已回滚到旧主题")]
        [InlineData(
            ThemeStudioForm.SettingsRollbackState.Failed,
            "Settings 回滚失败，磁盘可能仍指向新主题")]
        [InlineData(
            ThemeStudioForm.SettingsRollbackState.Unknown,
            "Settings 回滚状态未知")]
        [InlineData(
            ThemeStudioForm.SettingsRollbackState.NotAttempted,
            "")]
        public void ApplySession_RollbackStateIsVisibleWithoutHidingRuntimeError(
            ThemeStudioForm.SettingsRollbackState rollback,
            string expectedRollbackText)
        {
            RunSta(() =>
            {
                bool rollbackAttempted =
                    rollback != ThemeStudioForm.SettingsRollbackState.NotAttempted;
                string error = rollbackAttempted ? "runtime-failed" : "settings-save-failed";
                var gateway = new FixedApplyGateway(new ThemeStudioForm.ThemeApplyResult(
                    Success: false,
                    Stage: rollbackAttempted
                        ? ThemeStudioForm.ThemeApplyStage.ThemeApplied
                        : ThemeStudioForm.ThemeApplyStage.SettingsPersisted,
                    Error: error,
                    Revision: 9,
                    SettingsRollback: rollback));
                using var form = new ThemeStudioForm(gateway);
                form.OpenThemeForTesting(ThemeName);

                form.ApplyWithoutDialogsForTesting();

                string status = form.SavedStatusTextForTesting;
                Assert.Contains(
                    rollbackAttempted ? "运行时应用失败" : "Settings 持久化失败",
                    status,
                    StringComparison.Ordinal);
                Assert.Contains(error, status, StringComparison.Ordinal);
                if (expectedRollbackText.Length > 0)
                {
                    Assert.Contains(expectedRollbackText, status, StringComparison.Ordinal);
                }
                else
                {
                    Assert.DoesNotContain("已回滚", status, StringComparison.Ordinal);
                    Assert.DoesNotContain("回滚失败", status, StringComparison.Ordinal);
                    Assert.DoesNotContain("回滚状态未知", status, StringComparison.Ordinal);
                }
            });
        }

        [Fact]
        public void Gateway_WhenRollbackWriteFails_ReportsRollbackFailureWithoutHidingRuntimeError()
        {
            RunSta(() =>
            {
                var cfg = new Settings { Skin = "original-theme", GpuThemeEffectsEnabled = false };
                Assert.True(cfg.TrySave());

                int saveCalls = 0;
                SettingsHelper.BeforeSaveGateForTests = () =>
                {
                    if (Interlocked.Increment(ref saveCalls) == 2)
                    {
                        SettingsHelper.GlobalBlockSave = true;
                    }
                };

                var gateway = new ThemeStudioLauncher.AppActionsThemeApplyGateway(
                    cfg,
                    null,
                    null!,
                    (settings, _, _, revision) =>
                    {
                        settings.Skin = ThemeName;
                        return new ApplyOperationResult(
                            OperationId: "theme-test",
                            RequestRevision: revision,
                            EffectiveRevision: null,
                            OverallStatus: ApplyOperationStatus.Failed,
                            Modules:
                            [
                                ModuleApplyResult.Fail(
                                    "ThemeRuntime",
                                    ThemeName,
                                    effective: null,
                                    reason: "runtime-failed")
                            ],
                            FailureCode: "runtime-failed");
                    });
                ThemeStudioForm.ThemeApplyResult result = gateway.ApplySavedTheme(ThemeName, 9);

                Assert.False(result.Success);
                Assert.Equal(ThemeStudioForm.ThemeApplyStage.ThemeApplied, result.Stage);
                Assert.Equal(ThemeStudioForm.SettingsRollbackState.Failed, result.SettingsRollback);
                Assert.Contains("runtime-failed", result.Error, StringComparison.Ordinal);
                if (result.Stage == ThemeStudioForm.ThemeApplyStage.ThemeApplied)
                {
                    Assert.True(
                        result.SettingsRollback == ThemeStudioForm.SettingsRollbackState.Failed,
                        $"rollback={result.SettingsRollback}, stage={result.Stage}, " +
                        $"saveCalls={saveCalls}, error={result.Error}");
                }

                Assert.Equal("original-theme", cfg.Skin);
                Assert.Equal(ThemeName, ReadPersistedSkin());
                Assert.DoesNotContain("已完全回滚", result.Error, StringComparison.Ordinal);
            });
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void Gateway_RollbackPersistenceOutcome_IsReportedAndKeepsMemoryConsistent(
            bool rollbackReturns,
            bool rollbackThrows)
        {
            RunSta(() =>
            {
                var cfg = new Settings { Skin = "original-theme", GpuThemeEffectsEnabled = false };
                Assert.True(cfg.TrySave());

                var gateway = new ThemeStudioLauncher.AppActionsThemeApplyGateway(
                    cfg,
                    null,
                    null!,
                    (_, _, _, revision) => new ApplyOperationResult(
                        OperationId: "theme-test",
                        RequestRevision: revision,
                        EffectiveRevision: null,
                        OverallStatus: ApplyOperationStatus.Failed,
                        Modules:
                        [
                            ModuleApplyResult.Fail(
                                "ThemeRuntime",
                                ThemeName,
                                effective: null,
                                reason: "runtime-failed")
                        ],
                        FailureCode: "runtime-failed"),
                    settings =>
                    {
                        if (rollbackThrows) throw new IOException("rollback-write-failed");
                        return rollbackReturns && settings.TrySave();
                    });

                ThemeStudioForm.ThemeApplyResult result = gateway.ApplySavedTheme(ThemeName, 10);

                Assert.False(result.Success);
                Assert.Equal(ThemeStudioForm.ThemeApplyStage.ThemeApplied, result.Stage);
                Assert.Equal(
                    rollbackThrows
                        ? ThemeStudioForm.SettingsRollbackState.Unknown
                        : ThemeStudioForm.SettingsRollbackState.Restored,
                    result.SettingsRollback);
                Assert.Contains("runtime-failed", result.Error, StringComparison.Ordinal);
                Assert.Equal("original-theme", cfg.Skin);
                Assert.Equal(
                    rollbackReturns ? "original-theme" : ThemeName,
                    ReadPersistedSkin());
            });
        }

        [Fact]
        public void Gateway_WhenInitialSettingsSaveFails_DoesNotApplyOrAttemptRollback()
        {
            RunSta(() =>
            {
                var cfg = new Settings { Skin = "original-theme", GpuThemeEffectsEnabled = false };
                Assert.True(cfg.TrySave());

                int runtimeCalls = 0;
                var gateway = new ThemeStudioLauncher.AppActionsThemeApplyGateway(
                    cfg,
                    null,
                    null!,
                    (_, _, _, revision) =>
                    {
                        runtimeCalls++;
                        return new ApplyOperationResult(
                            OperationId: "unexpected-runtime",
                            RequestRevision: revision,
                            EffectiveRevision: null,
                            OverallStatus: ApplyOperationStatus.Applied,
                            Modules: []);
                    });

                SettingsHelper.GlobalBlockSave = true;
                ThemeStudioForm.ThemeApplyResult result = gateway.ApplySavedTheme(ThemeName, 11);

                Assert.False(result.Success);
                Assert.Equal(ThemeStudioForm.ThemeApplyStage.SettingsPersisted, result.Stage);
                Assert.Equal(ThemeStudioForm.SettingsRollbackState.NotAttempted, result.SettingsRollback);
                Assert.Equal(0, runtimeCalls);
                Assert.Equal("original-theme", cfg.Skin);
                Assert.Equal("original-theme", ReadPersistedSkin());
            });
        }

        private static void RunSta(Action action)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Theme Studio API seam did not finish within 30 seconds.");
            }

            if (failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private static string? ReadPersistedSkin()
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsHelper.FilePath));
            return document.RootElement.TryGetProperty("Skin", out JsonElement skin)
                ? skin.GetString()
                : null;
        }

        private sealed class CountingApplyGateway : ThemeStudioForm.IThemeApplyGateway
        {
            public int CallCount { get; private set; }

            public ThemeStudioForm.ThemeApplyResult ApplySavedTheme(string themeName, long revision)
            {
                CallCount++;
                return new ThemeStudioForm.ThemeApplyResult(
                    Success: true,
                    Stage: ThemeStudioForm.ThemeApplyStage.ThemeApplied,
                    EffectiveTheme: themeName,
                    Revision: revision);
            }
        }

        private sealed class FixedApplyGateway(
            ThemeStudioForm.ThemeApplyResult result)
            : ThemeStudioForm.IThemeApplyGateway
        {
            public ThemeStudioForm.ThemeApplyResult ApplySavedTheme(
                string themeName,
                long revision) => result;
        }
    }
}
