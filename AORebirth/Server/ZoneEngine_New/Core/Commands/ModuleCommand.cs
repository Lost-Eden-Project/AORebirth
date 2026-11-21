namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Globalization;
    using System.Linq;
    using System.Threading.Tasks;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Modules;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// <c>.module list</c> and <c>.module reload &lt;id&gt; [dryrun]</c>: the loaded modules, and a hot reload of one from its
    /// own folder (<see cref="ZoneModuleHost.ReloadAsync"/>). Reload needs Modules.json HotReload enabled and a module
    /// that opts in; anything else is refused with the reason and changes nothing.
    /// </summary>
    public sealed class ModuleCommand : IGmCommand
    {
        readonly ZoneModuleHost _modules;

        public ModuleCommand(ZoneModuleHost modules)
        {
            _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        }

        public string Name => "module";

        public int RequiredGmLevel => 1000;

        public string Usage => ".module list | .module reload <moduleId> [dryrun]";

        public GmCommandHelp Help { get; } = new(
            "Lists the zone's modules and hot-reloads one.",
            [
                new(".module list", "Every loaded module with its version, generation and whether it can be hot-reloaded."),
                new(".module reload <moduleId>", "Stops the module, unloads it and loads the version now in its folder, without a restart."),
                new(".module reload <moduleId> dryrun", "Only the checks: says whether the reload would run, or why not."),
            ],
            [
                new(".module reload AntiCheat", "Load a new AntiCheat build copied into Modules/AntiCheat."),
            ],
            "Needs Modules.json \"HotReload\": { \"Enabled\": true } and SupportsHotReload in the module's module.json. "
            + "Required modules and the module owning the connection filter or handoff hook are refused. While it reloads "
            + "(usually a second or two) the module sees no events; in-memory state it does not export is lost.");

        public void Execute(GmCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            string[] args = context.Args;
            if (args.Length == 1 && string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
            {
                var statuses = _modules.ListModules();
                if (statuses.Count == 0)
                {
                    GmCommandFeedback.Send(context.Session, context.Player, "No modules are loaded.");
                    return;
                }

                GmCommandFeedback.Send(context.Session, context.Player, "Hot reload " + (_modules.HotReload.Enabled ? "enabled" : "disabled") + ":");
                foreach (ModuleStatus status in statuses)
                    GmCommandFeedback.Send(context.Session, context.Player, string.Format(CultureInfo.InvariantCulture,
                        "{0} v{1} gen={2} reload={3} sha={4}", status.Id, status.Version, status.Generation,
                        status.HotReloadable ? "yes" : "no", status.Sha256.Length > 12 ? status.Sha256[..12] : status.Sha256));
                return;
            }

            bool dryRun = args.Length == 3 && string.Equals(args[2], "dryrun", StringComparison.OrdinalIgnoreCase);
            if (args.Length is < 2 or > 3 || !string.Equals(args[0], "reload", StringComparison.OrdinalIgnoreCase) || (args.Length == 3 && !dryRun))
            {
                context.SendUsage();
                return;
            }

            Player gm = context.Player;
            string by = "gm:" + (gm.Name ?? gm.Identity.Instance.ToString(CultureInfo.InvariantCulture));
            GmCommandFeedback.Send(context.Session, gm, (dryRun ? "Checking a reload of " : "Reloading module ") + args[1] + "...");
            _ = _modules.ReloadAsync(args[1], sourceDirectory: null, dryRun, by).ContinueWith(task =>
            {
                string line = task.IsCompletedSuccessfully ? task.Result.Describe() : "failed:unexpected restored=no";
                if (gm.Session is IZoneSession && gm.Playfield is Playfield here)
                    here.DispatchPlayerProjection(gm, () =>
                    {
                        if (gm.Session is IZoneSession current)
                            GmCommandFeedback.Send(current, gm, "Module reload: " + line);
                    });
            }, TaskScheduler.Default);
        }
    }
}
