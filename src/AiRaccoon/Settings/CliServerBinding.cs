using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Watch;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Settings;

/// <summary>
///     Write-exclusivity for the CLI graph (ADR-0075 §5.3): every control-plane store a command
///     resolves is the one server-backed instance, overriding the bank-backed registrations.
/// </summary>
internal static class CliServerBinding
{
    extension(IServiceCollection services)
    {
        /// <summary>Binds every store the server answers for to <paramref name="store" />, so a CLI command never opens the bank.</summary>
        internal void BindCliToServer(LazyServerSettingsStore store)
        {
            Guard.IsNotNull(store);
            services.AddSingleton<ISettingsStore>(store);
            services.AddSingleton<IModelMigrationStore>(store);
            services.AddSingleton<ICodeEngineStore>(store);
            services.AddSingleton<IRepairStore>(store);
            services.AddSingleton<IPromotionQueuePruneStore>(store);
            services.AddSingleton<IMaintenanceStatsStore>(store);
            services.AddSingleton<INoiseSummaryStore>(store);
            services.AddSingleton<IWatchRegisteredStore>(store);
            services.AddSingleton<IProjectDirectory>(store);
        }
    }
}
