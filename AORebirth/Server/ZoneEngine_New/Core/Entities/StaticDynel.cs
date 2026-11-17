namespace ZoneEngine_New.Core.Entities
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Enums;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Inventory;

    using MsgQuaternion = LostEden.Protocol.GameData.Quaternion;
    using MsgVector3 = LostEden.Protocol.GameData.Vector3;

    /// <summary>
    /// Playfield-placed world object backed by a reference-only interpolated item template.
    /// </summary>
    public abstract class StaticDynel : Dynel, IUsableDynel
    {
        const int SimpleItemFullUpdateUnknown1Type = 1000015;
        const byte SimpleItemFullUpdateUnknown3 = 0x6F;
        const int SimpleItemFullUpdateMsgVersion = 0x0b;

        protected StaticDynel(Identity identity, ItemTemplate template)
            : base(identity)
        {
            ArgumentNullException.ThrowIfNull(template);
            Template = template;
            ApplyTemplateStats();
        }

        public ItemTemplate Template { get; }

        /// <summary>
        /// The item template low id this object reads as for stat 702 (Item Template ID) when an item use's criteria
        /// test it as the selected target. An interpolated template at its top QL carries the high record's id.
        /// </summary>
        public virtual int ItemTemplateLowId => Template.Id;

        /// <summary>The QL this object reads as for stat 54 (Level) when an item use's criteria test it as the selected target.</summary>
        public virtual int ItemQuality => Template.Quality;

        /// <summary>Instance-local name from authored state-entry actions; shared templates remain immutable.</summary>
        public string? RuntimeName { get; set; }

        /// <summary>
        /// Template OnUse runs after the template's AttackDelay, like an inventory use, through the
        /// playfield's <see cref="ItemUseService"/>. <see cref="DelaysUse"/> false runs at once.
        /// </summary>
        public bool TryUse(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);

            ItemUseService? uses = DelaysUse ? Playfield?.GetService<ItemUseService>() : null;
            if (uses != null)
                return uses.TryBegin(player, this) != ItemUseStart.Rejected;

            // As the delayed path does: an unmet ToUse requirement (a shop terminal's level gate) tells the user why.
            if (!CanBeginUse(player))
            {
                RequirementFeedback.SendIfUnmet(player, Template, ActionType.ToUse, resolve: UseResolver(player));
                return false;
            }

            return ExecuteUse(player);
        }

        /// <summary>False for uses that open a UI rather than run template spells.</summary>
        protected virtual bool DelaysUse => true;

        /// <summary>Gates checked when a use is asked for: <see cref="CanStillUse"/>, within reach and the ToUse requirements.</summary>
        public bool CanBeginUse(Player player)
            => CanStillUse(player)
                && (Playfield?.GetService<WorldSimulation.AreaTriggerService>()?.IsUseEnabled(this) ?? true)
                && GetEdgeDistanceTo(player) <= LootableDynel.OpenRange
                && Template.MeetsActionRequirements(stat => player.Stats.Get(stat), ActionType.ToUse, UseResolver(player));

        /// <summary>
        /// The user's answer to ToUse leaves a stat cannot settle: running nanos (the teleport pad 258912 wants
        /// HasNotRunningNano 259452, its own 30 s lockout), perks, worn items. Without it such a leaf fails closed.
        /// </summary>
        public Func<ItemRequirement, bool?> UseResolver(Player player)
            => ItemTemplate.WithActionTarget(player.ResolvePerkRequirement, player, this);

        /// <summary>
        /// Gates checked again when a delayed use completes: the user is on this object's playfield. Reach and ToUse
        /// requirements met when the use was asked for are not checked again.
        /// </summary>
        public bool CanStillUse(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);

            if (player.Session == null || Playfield == null)
                return false;

            if (player.Playfield == null
                || player.Playfield.Identity.Instance != Playfield.Identity.Instance)
                return false;

            return true;
        }

        /// <summary>Runs the use. Callers gate with <see cref="CanBeginUse"/>.</summary>
        public bool ExecuteUse(Player player) => OnUse(player);

        protected virtual bool OnUse(Player player)
        {
            if (Playfield == null || player.Session == null)
                return false;

            if (Playfield.GetService<WorldSimulation.AreaTriggerService>() is { } triggers)
                return triggers.BeginUse(this, player);

            return Template.ExecuteOnUseSpells(
                player,
                Playfield.GetRequiredService<IInventoryRepository>(),
                Playfield.GetRequiredService<IItemBuilder>());
        }

        public override MessageBody BuildSpawnMessage()
        {
            MsgVector3 coordinate = Position;
            MsgQuaternion heading = Rotation;

            // A world static has no owner. The owner fields must be 0:0: the codec writes Coordinate/Heading only for
            // an ownerless item, and the client has no Dynels.dat position for a runtime instance (retail
            // capture_mangohip_lifts, lift platform/button SimpleItemFullUpdate).
            var message = new SimpleItemFullUpdateMessage
            {
                Identity = Identity,
                Unknown = 0,
                Unknown1 = SimpleItemFullUpdateMsgVersion,
                OwnerType = 0,
                OwnerInstance = 0,
                Position = coordinate,
                Rotation = heading,
                PlayfieldId = Playfield != null ? Playfield.ClientPlayfieldId : 0,
                StateMachine = new Identity((IdentityType)SimpleItemFullUpdateUnknown1Type, 0),
                // The two bytes after the state machine: 0, then the byte the captures carry.
                Unknown2 = (short)SimpleItemFullUpdateUnknown3,
                Stats = BuildStats(),
                // Sent as a C string whose length counts the NUL the codec adds, as for a mission key. Empty stays
                // length 0.
                Name = RuntimeName ?? Template.Name ?? string.Empty
            };
            return message;
        }

        void ApplyTemplateStats()
        {
            foreach (KeyValuePair<CharacterStat, int> pair in Template.Stats)
                Stats.Set(pair.Key, pair.Value);

            Stats.Set(CharacterStat.ACGItemTemplateID, Template.Id);
            Stats.Set(CharacterStat.ACGItemTemplateID2, Template.Id);
            Stats.Set(CharacterStat.StaticInstance, Template.Id);
        }

        protected virtual GameTuple<Stat, int>[] BuildStats()
        {
            var stats = new List<GameTuple<Stat, int>>();
            foreach ((CharacterStat stat, int _, int _, int full) in Stats.GetEntries())
            {
                stats.Add(new GameTuple<Stat, int>((Stat)stat, full));
            }

            return stats.ToArray();
        }
    }

    /// <summary>Default Dynels.dat static: runs template OnUse spells.</summary>
    public sealed class PlayfieldStaticDynel : StaticDynel
    {
        public PlayfieldStaticDynel(Identity identity, ItemTemplate template)
            : base(identity, template)
        {
        }
    }
}
