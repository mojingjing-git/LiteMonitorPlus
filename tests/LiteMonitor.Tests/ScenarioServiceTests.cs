using System;
using System.Collections.Generic;
using System.IO;
using LiteMonitor;
using LiteMonitor.Core.Actions.Services;
using LiteMonitor.src.Core.Scenarios;
using Xunit;

namespace LiteMonitor.Tests
{
    public sealed class ScenarioServiceTests : IDisposable
    {
        private readonly string _dir;
        private readonly ScenarioProfileStore _store;

        public ScenarioServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "litemonitor-scenario-service-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _store = new ScenarioProfileStore(Path.Combine(_dir, "profiles.json"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void CreateFromCurrent_PersistsProjection()
        {
            var settings = new Settings { RefreshMs = 1500 };
            var service = new ScenarioService(_store);

            var created = service.CreateFromCurrent("Work", settings);

            Assert.True(created.Success);
            var loaded = Assert.Single(service.List());
            Assert.Equal("Work", loaded.Name);
            Assert.Equal(1500, loaded.Settings.RefreshMs);
        }

        [Fact]
        public void Rename_UsesCurrentRevision()
        {
            var service = new ScenarioService(_store);
            var created = service.CreateFromCurrent("Work", new Settings());
            Assert.True(created.Success);

            var renamed = service.Rename(created.Profile!, "Office");

            Assert.True(renamed.Success);
            Assert.Equal("Office", renamed.Profile!.Name);
            Assert.Equal(created.Profile!.DefinitionRevision + 1, renamed.Profile.DefinitionRevision);
        }

        [Fact]
        public void Apply_PersistsCandidateAndUsesInjectedModules()
        {
            var settings = new Settings { RefreshMs = 1000 };
            var service = new ScenarioService(_store);
            var created = service.CreateFromCurrent("Work", settings);
            Assert.True(created.Success);

            settings.RefreshMs = 2000;
            Settings? saved = null;
            Settings? appliedLive = null;
            Settings? appliedCandidate = null;
            var coordinator = new ScenarioService(
                _store,
                candidate => { saved = candidate; return true; },
                (live, candidate) => { appliedLive = live; appliedCandidate = candidate; return new[] { ModuleApplyResult.Ok("Ui", "saved", "applied") }; });

            var outcome = coordinator.Apply(created.Profile!, settings);

            Assert.Equal(ApplyOperationStatus.Applied, outcome.Operation.OverallStatus);
            Assert.NotNull(saved);
            Assert.Equal(1000, saved!.RefreshMs);
            Assert.Same(settings, appliedLive);
            Assert.Equal(1000, appliedCandidate!.RefreshMs);
        }

        [Fact]
        public void Apply_SaveFailure_DoesNotRunModules()
        {
            var settings = new Settings { RefreshMs = 1000 };
            var service = new ScenarioService(_store);
            var created = service.CreateFromCurrent("Work", settings);
            settings.RefreshMs = 2000;
            bool applied = false;

            var failing = new ScenarioService(_store, _ => false, (_, _) => { applied = true; return Array.Empty<ModuleApplyResult>(); });
            var outcome = failing.Apply(created.Profile!, settings);

            Assert.Equal(ApplyOperationStatus.Failed, outcome.Operation.OverallStatus);
            Assert.False(applied);
            Assert.Equal(2000, settings.RefreshMs);
        }

        [Fact]
        public void Delete_RemovesProfile()
        {
            var service = new ScenarioService(_store);
            var created = service.CreateFromCurrent("Work", new Settings());
            Assert.True(created.Success);

            var deleted = service.Delete(created.Profile!);

            Assert.True(deleted.Success);
            Assert.Empty(service.List());
        }
    }
}
