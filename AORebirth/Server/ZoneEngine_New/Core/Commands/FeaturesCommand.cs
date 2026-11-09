namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading.Tasks;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Mobs;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// <c>.features</c>: shows and edits an NPC hash's behaviour switches (<see cref="NpcFeature"/>) in
    /// MonsterCombatRules.json, e.g. <c>.features add noxp</c>. An edit is written to the file the zone reads, reloads the
    /// rules here and on every other zone instance, and reaches each NPC of that hash when it next spawns
    /// (<c>.npc respawn</c> does it now).
    /// </summary>
    public sealed class FeaturesCommand : IGmCommand
    {
        /// <summary>Another zone instance edited MonsterCombatRules.json: read it again.</summary>
        const string ReloadCommand = "gamedata.monsterCombatRules";

        readonly IGameData _gameData;
        readonly IZoneLogger _logger;
        readonly ZoneEngine_New.Core.World.WorldCommands? _commands;

        public FeaturesCommand(IGameData gameData, IZoneLogger logger, ZoneEngine_New.Core.World.WorldCommands? commands = null)
        {
            ArgumentNullException.ThrowIfNull(gameData);
            ArgumentNullException.ThrowIfNull(logger);
            _gameData = gameData;
            _logger = logger;
            _commands = commands;
            _commands?.Register(ReloadCommand, _ => OnEditedElsewhere());
        }

        public string Name => "features";

        public int RequiredGmLevel => 1;

        public string Usage => ".features [add|remove <feature> [hash]] | list";

        public GmCommandHelp Help { get; } = new(
            "Shows or edits an NPC's features (MonsterCombatRules.json), for every NPC of its hash at every level. "
            + "Saved to the file and applied to new spawns on every zone instance.",
            [
                new(".features", "Your selected NPC's features: what it has now and what its hash has in the file."),
                new(".features add <feature> [hash]", "Gives your selected NPC's hash (or the hash given) the feature."),
                new(".features remove <feature> [hash]", "Takes the feature off."),
                new(".features list", "Every feature name."),
            ],
            [
                new(".features add noxp", "Your target's kind stops giving kill XP (regular and alien)."),
                new(".features remove noxp ANOV", "Hash ANOV gives kill XP again."),
            ],
            "Feature names are not case-sensitive. NPCs already spawned keep their features until they respawn: "
            + ".npc respawn applies the change to your target now.");

        public void Execute(GmCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            string[] args = context.Args;
            if (args.Length == 0)
            {
                if (TryResolveNpc(context, out NpcCharacter npc))
                    ReportFeatures(context, npc);
                return;
            }

            string verb = args[0].ToLowerInvariant();
            if (verb == "list" && args.Length == 1)
            {
                GmCommandFeedback.Send(context.Session, context.Player,
                    "Features: " + string.Join(", ", Enum.GetNames<NpcFeature>()));
                return;
            }

            if (verb is not ("add" or "remove") || args.Length is < 2 or > 3)
            {
                context.SendUsage();
                return;
            }

            if (!TryParseFeature(args[1], out NpcFeature feature))
            {
                context.SendUsage("Unknown feature '" + args[1] + "'. Features: " + string.Join(", ", Enum.GetNames<NpcFeature>()));
                return;
            }

            bool add = verb == "add";
            string hash;
            string? name;
            if (args.Length == 3)
            {
                hash = args[2].Trim();
                bool known = _gameData.TryGetMobTemplate(hash, out MobTemplate template);
                // Removing is always allowed, so a feature left on a hash since removed from NpcTemplates can be cleared.
                if (!known && add)
                {
                    GmCommandFeedback.SendError(context.Session, context.Player, "No NpcTemplates.json template " + hash + ".");
                    return;
                }

                if (known && !string.IsNullOrEmpty(template.Hash))
                    hash = template.Hash;
                name = known ? template.Name : null;
            }
            else
            {
                if (!TryResolveNpc(context, out NpcCharacter npc))
                    return;
                if (npc.MobTemplate is not { Hash.Length: > 0 } template)
                {
                    GmCommandFeedback.SendError(context.Session, context.Player,
                        (npc.Name ?? "Target") + " has no mob template; give a hash.");
                    return;
                }

                hash = template.Hash;
                name = string.IsNullOrWhiteSpace(template.Name) ? npc.Name : template.Name;
            }

            Edit(context, hash, name, feature, add);
        }

        /// <summary>Writes off the tick, tells the other instances, then reports on the GM's own tick.</summary>
        void Edit(GmCommandContext context, string hash, string? name, NpcFeature feature, bool add)
        {
            Player gm = context.Player;
            string gmName = gm.Name ?? gm.Identity.Instance.ToString(CultureInfo.InvariantCulture);
            string root = _gameData.RootPath;
            string label = hash + (string.IsNullOrWhiteSpace(name) ? string.Empty : " (" + name + ")");
            _ = Task.Run(async () =>
            {
                string line;
                bool failed = false;
                try
                {
                    FeatureEditResult result = MonsterCombatRulesCatalog.SetFeature(root, hash, feature, add);
                    if (!result.Ok)
                    {
                        failed = true;
                        line = "Not saved: " + result.Problem;
                    }
                    else if (!result.Changed)
                    {
                        line = label + (add ? " already has " : " does not have ") + feature + ".";
                    }
                    else
                    {
                        _logger.Info("MonsterCombatRules: " + gmName + (add ? " added " : " removed ") + feature
                            + (add ? " to " : " from ") + label + " in " + result.File);
                        bool told = _commands == null || await _commands.BroadcastAsync(ReloadCommand).ConfigureAwait(false);
                        line = label + (add ? " now has " : " no longer has ") + feature
                            + ". New spawns get it; .npc respawn applies it to your target now."
                            + (told ? string.Empty : " Other zone instances were not reached; they pick it up on a reload or restart.");
                    }
                }
                catch (Exception exception)
                {
                    failed = true;
                    line = "Not saved: " + exception.GetType().Name + ": " + exception.Message;
                }

                gm.Playfield?.DispatchPlayerProjection(gm, () =>
                {
                    if (failed)
                        GmCommandFeedback.SendError(context.Session, gm, line);
                    else
                        GmCommandFeedback.Send(context.Session, gm, line);
                });
            });
        }

        void OnEditedElsewhere()
        {
            AORebirth.Modules.Content.GameDataReloadResult result = MonsterCombatRulesCatalog.Reload(_gameData.RootPath);
            if (result.Ok)
                _logger.Info("MonsterCombatRules: reloaded after an edit on another zone instance; hashes=" + result.Entries);
            else
                _logger.Warn("MonsterCombatRules: reload after an edit elsewhere failed: " + string.Join("; ", result.Problems));
        }

        void ReportFeatures(GmCommandContext context, NpcCharacter npc)
        {
            string name = npc.Name ?? npc.Identity.Instance.ToString(CultureInfo.InvariantCulture);
            MobTemplate? template = npc.MobTemplate;
            if (template == null || string.IsNullOrEmpty(template.Hash))
            {
                GmCommandFeedback.Send(context.Session, context.Player, name + " has no mob template.");
                return;
            }

            string live = Describe(template.Features);
            IReadOnlyCollection<NpcFeature> fileFeatures =
                MonsterCombatRulesCatalog.For(_gameData.RootPath).TryGet(template.Hash, out MonsterCombatRules rules)
                    && rules.Features != null
                    ? rules.Features
                    : [];
            string file = Describe(fileFeatures);
            var lines = new List<string>
            {
                name + " (" + template.Hash + ") has: " + live,
                "MonsterCombatRules.json " + template.Hash + ": " + file
            };

            // Boss (boss flag) and NoCombat (blue name, placeholder) can come from the template itself, not the file.
            if (fileFeatures.Any(feature => !template.Has(feature)))
                lines.Add("It spawned before the file changed; .npc respawn gives it the file's features.");
            GmCommandFeedback.SendLines(context.Session, context.Player, lines);
        }

        static string Describe(IEnumerable<NpcFeature> features)
        {
            string text = string.Join(", ", features.Select(feature => feature.ToString()).Order(StringComparer.Ordinal));
            return text.Length == 0 ? "none" : text;
        }

        /// <summary>A feature by name, any case; numbers are not names.</summary>
        static bool TryParseFeature(string text, out NpcFeature feature)
        {
            feature = default;
            return !string.IsNullOrWhiteSpace(text)
                && char.IsLetter(text.Trim()[0])
                && Enum.TryParse(text.Trim(), ignoreCase: true, out feature)
                && Enum.IsDefined(feature);
        }

        static bool TryResolveNpc(GmCommandContext context, out NpcCharacter npc)
        {
            npc = null!;
            Playfield? playfield = context.Player.Playfield;
            if (playfield == null)
            {
                GmCommandFeedback.Send(context.Session, context.Player, "Not on a playfield.");
                return false;
            }

            if (context.Player.Target == Identity.None)
            {
                GmCommandFeedback.Send(context.Session, context.Player, "Select an NPC, or give a hash.");
                return false;
            }

            DynelRegistry registry = playfield.GetRequiredService<DynelRegistry>();
            if (!registry.TryGet(context.Player.Target, out Dynel? dynel) || dynel is not NpcCharacter target)
            {
                GmCommandFeedback.Send(context.Session, context.Player, "Target is not an NPC.");
                return false;
            }

            npc = target;
            return true;
        }
    }
}
