namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Globalization;

    using ZoneEngine_New.Core.Lifecycle;

    /// <summary><c>.shutdown &lt;seconds&gt;</c> schedules a timed engine shutdown with a chat countdown; <c>.shutdown abort</c> cancels it.</summary>
    public sealed class ShutdownCommand : IGmCommand
    {
        readonly ShutdownScheduler _scheduler;

        public ShutdownCommand(ShutdownScheduler scheduler)
        {
            ArgumentNullException.ThrowIfNull(scheduler);
            _scheduler = scheduler;
        }

        public string Name => "shutdown";

        /// <summary>Stopping the whole engine is reserved for top-level GMs.</summary>
        public int RequiredGmLevel => 1000;

        public string Usage => ".shutdown <seconds> | .shutdown abort";

        public GmCommandHelp Help { get; } = new(
            "Schedules a timed engine shutdown with a chat countdown.",
            [
                new(".shutdown <seconds>", "Shuts the engine down after the given seconds."),
                new(".shutdown abort", "Cancels a scheduled shutdown."),
            ],
            [
                new(".shutdown 300", "Shut down in 5 minutes."),
                new(".shutdown abort", "Cancel it."),
            ]);

        public void Execute(GmCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            string issuer = string.IsNullOrEmpty(context.Player.Name)
                ? context.Player.Identity.Instance.ToString(CultureInfo.InvariantCulture)
                : context.Player.Name;

            if (context.Args.Length == 1 && string.Equals(context.Args[0], "abort", StringComparison.OrdinalIgnoreCase))
            {
                if (!_scheduler.Abort(issuer))
                    GmCommandFeedback.Send(context.Session, context.Player, "No shutdown is scheduled.");
                return;
            }

            if (context.Args.Length != 1
                || !int.TryParse(context.Args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
                || seconds < 1
                || seconds > ShutdownScheduler.MaxSeconds)
            {
                context.SendUsage(string.Format(CultureInfo.InvariantCulture,
                    "Usage: {0} (1 to {1} seconds)", Usage, ShutdownScheduler.MaxSeconds));
                return;
            }

            _scheduler.Schedule(seconds, issuer);
        }
    }
}
