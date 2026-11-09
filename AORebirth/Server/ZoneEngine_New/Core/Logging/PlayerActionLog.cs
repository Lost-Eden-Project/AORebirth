namespace ZoneEngine_New.Core.Logging
{
    using System;
    using System.Globalization;

    using NLog;
    using NLog.Config;
    using NLog.Targets;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using Utility;

    using ZoneEngine_New.Core.Entities;

    /// <summary>
    /// A file of its own for what players asked the server to do and what the server answered: IP spent and returned,
    /// resets, refused actions, plus the IP the character carries into each zone. Always on (not behind a
    /// <see cref="DebugInfoDetail"/>), so a player report can be read against the server's view afterwards. Lines go
    /// only to this file, not the console or the engine log, and behind the same background writer as every other
    /// target (<see cref="AsyncLogging"/>), so a write from a tick only queues.
    /// </summary>
    public static class PlayerActionLog
    {
        public const string LoggerName = "PlayerActions";

        static readonly Logger Log = LogManager.GetLogger(LoggerName);

        /// <summary>Adds the file and its rule. Call after the engine's own targets and before <see cref="AsyncLogging.Install"/>.</summary>
        public static void Configure(string fileName)
        {
            LoggingConfiguration config = LogManager.Configuration ?? new LoggingConfiguration();
            var file = new FileTarget("player-actions")
            {
                FileName = fileName,
                Layout = "${longdate} ${message}",
                ArchiveAboveSize = LogUtil.LogFileArchiveAboveBytes,
                MaxArchiveFiles = LogUtil.LogFileMaxArchiveFiles
            };
            config.AddTarget(file);
            // First and final: these lines stay out of the console, the engine log and the debug ring.
            config.LoggingRules.Insert(0, new LoggingRule(LoggerName, LogLevel.Info, file) { Final = true });
            LogManager.Configuration = config;
        }

        /// <summary>One line: who, where, what, then <paramref name="detail"/> as key=value pairs.</summary>
        public static void Write(Player player, string action, string detail)
        {
            if (!Log.IsInfoEnabled)
                return;

            Log.Info(string.Format(
                CultureInfo.InvariantCulture,
                "char={0} name={1} pf={2} {3} {4}",
                player.Identity.Instance,
                string.IsNullOrEmpty(player.Name) ? "?" : player.Name,
                player.Playfield?.Identity.Instance ?? 0,
                action,
                detail));
        }

        /// <summary>The character's IP book: what is left, what is spent, Reset Points and Full IP Resets.</summary>
        public static string IpState(Player player)
        {
            StatCollection stats = player.Stats;
            return string.Format(
                CultureInfo.InvariantCulture,
                "ip={0} usedIp={1} totalIp={2} rp={3} fullResets={4} freeFullResetUsed={5}",
                player.AvailableIp,
                stats.GetOrZero(CharacterStat.UsedIP, StatDetail.Base),
                stats.GetOrZero(CharacterStat.IP),
                stats.GetOrZero(CharacterStat.RP, StatDetail.Base),
                stats.GetOrZero(CharacterStat.FullIPRPoints, StatDetail.Base),
                (stats.GetOrZero(CharacterStat.SpecialCondition, StatDetail.Base)
                    & Characters.SkillTraining.FreeFullResetUsedBit) != 0 ? 1 : 0);
        }
    }
}
