namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    using ZoneEngine_New.Core.Quests.Dungeons.Static;

    /// <summary>
    /// <c>.staticdungeon list|enter &lt;entrance instance&gt;|reset &lt;instance playfield&gt;</c>: the catalog's
    /// entrances and the live instances, entering any catalog dungeon's shared instance from anywhere, and releasing an
    /// empty instance.
    /// </summary>
    public sealed class StaticDungeonCommand : IGmCommand
    {
        readonly StaticDungeonService _dungeons;

        public StaticDungeonCommand(StaticDungeonService dungeons)
        {
            ArgumentNullException.ThrowIfNull(dungeons);
            _dungeons = dungeons;
        }

        public string Name => "staticdungeon";

        public int RequiredGmLevel => 1;

        public string Usage => ".staticdungeon list | enter <entrance instance> | reset <instance playfield>";

        public GmCommandHelp Help { get; } = new(
            "Static dungeons: the fixed ACG dungeons behind world doors (GameData/Dungeons/Static).",
            [
                new(".staticdungeon list", "The catalog's entrances (with what their files left out) and the live instances on this zone: playfield, entrance, owner, players, NPCs left."),
                new(".staticdungeon enter <entrance instance>", "Enter that dungeon's shared instance from anywhere; the range check is skipped."),
                new(".staticdungeon reset <instance playfield>", "Release that instance now if nobody is inside; the next entry builds a fresh one."),
            ],
            [
                new(".staticdungeon list", "See which doors have dungeons and which instances run."),
                new(".staticdungeon enter 3221360854", "Go into the dungeon behind the Nascence door 3221360854."),
            ],
            "Edited files apply after the StaticDungeons GameData reload, to instances built after it.");

        public void Execute(GmCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.Args.Length == 0)
            {
                context.SendUsage();
                return;
            }

            switch (context.Args[0].ToLowerInvariant())
            {
                case "list" when context.Args.Length == 1:
                    List(context);
                    return;
                case "enter" when context.Args.Length == 2
                    && uint.TryParse(context.Args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint entrance):
                    if (!_dungeons.TryEnterAsGm(context.Player, entrance))
                        GmCommandFeedback.SendError(context.Session, context.Player, "No static dungeon is behind entrance "
                            + entrance.ToString(CultureInfo.InvariantCulture) + " (or you cannot be moved right now).");
                    return;
                case "reset" when context.Args.Length == 2
                    && int.TryParse(context.Args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int playfield):
                    GmCommandFeedback.Send(context.Session, context.Player, _dungeons.TryReset(playfield)
                        ? "Static dungeon instance " + playfield.ToString(CultureInfo.InvariantCulture) + " released."
                        : "Static dungeon instance " + playfield.ToString(CultureInfo.InvariantCulture) + " is occupied or not running on this zone instance.");
                    return;
                default:
                    context.SendUsage();
                    return;
            }
        }

        void List(GmCommandContext context)
        {
            var text = new StringBuilder();
            IReadOnlyCollection<StaticDungeonBlueprint> dungeons = _dungeons.Catalog.Dungeons;
            text.Append("Static dungeons: ").Append(dungeons.Count.ToString(CultureInfo.InvariantCulture)).Append(" entrances");
            foreach (StaticDungeonBlueprint dungeon in dungeons)
            {
                text.Append('\n').Append(dungeon.Entrance.IdentityInstance.ToString(CultureInfo.InvariantCulture))
                    .Append(" pf ").Append(dungeon.Entrance.PlayfieldId.ToString(CultureInfo.InvariantCulture))
                    .Append(' ').Append(dungeon.Name)
                    .Append(": ").Append(dungeon.Layout.Generator.Rooms?.Count.ToString(CultureInfo.InvariantCulture) ?? "0").Append(" rooms, ")
                    .Append(dungeon.Npcs.Count.ToString(CultureInfo.InvariantCulture)).Append(" NPCs");
                if (dungeon.UnresolvedNpcs > 0)
                    text.Append(" (+").Append(dungeon.UnresolvedNpcs.ToString(CultureInfo.InvariantCulture)).Append(" without a template)");
            }
            IReadOnlyList<string> problems = _dungeons.Catalog.Problems;
            if (problems.Count > 0)
                text.Append("\nProblems: ").Append(problems.Count.ToString(CultureInfo.InvariantCulture)).Append(" (see the zone log or the StaticDungeons reload)");
            var live = _dungeons.LiveInstances();
            text.Append("\nLive instances here: ").Append(live.Count.ToString(CultureInfo.InvariantCulture));
            foreach ((int playfield, StaticDungeonBlueprint blueprint, string owner, int players, int npcsLeft) in live)
            {
                text.Append('\n').Append(playfield.ToString(CultureInfo.InvariantCulture))
                    .Append(" entrance ").Append(blueprint.Entrance.IdentityInstance.ToString(CultureInfo.InvariantCulture))
                    .Append(" (").Append(blueprint.Name).Append(") ").Append(owner)
                    .Append(", players ").Append(players.ToString(CultureInfo.InvariantCulture))
                    .Append(", NPCs left ").Append(npcsLeft.ToString(CultureInfo.InvariantCulture));
            }
            GmCommandFeedback.Send(context.Session, context.Player, text.ToString());
        }
    }
}
