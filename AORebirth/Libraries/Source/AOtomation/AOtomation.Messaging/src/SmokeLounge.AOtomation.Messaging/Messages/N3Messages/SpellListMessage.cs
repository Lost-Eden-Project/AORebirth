// --------------------------------------------------------------------------------------------------------------------
// <copyright file="SpellListMessage.cs" company="SmokeLounge">
//   Copyright © 2013 SmokeLounge.
//   This program is free software. It comes without any warranty, to
//   the extent permitted by applicable law. You can redistribute it
//   and/or modify it under the terms of the Do What The Fuck You Want
//   To Public License, Version 2, as published by Sam Hocevar. See
//   http://www.wtfpl.net/ for more details.
// </copyright>
// <summary>
//   Defines the SpellListMessage type.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace SmokeLounge.AOtomation.Messaging.Messages.N3Messages
{
    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Serialization;
    using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

    [AoContract((int)N3MessageType.SpellList)]
    public class SpellListMessage : N3Message
    {
        #region Constructors and Destructors

        public SpellListMessage()
        {
            this.N3MessageType = N3MessageType.SpellList;
        }

        #endregion

        #region AoMember Properties

        [AoMember(0, SerializeSize = ArraySizeType.X3F1)]
        public NanoEffect[] NanoEffects { get; set; }

        [AoMember(1)]
        public Identity Character { get; set; }

        /// <summary>
        /// Capture-backed nano display name (e.g. "Ambient Restoration").
        /// Serialized by SpellListMessageSerializer after Character when set.
        /// </summary>
        public string NanoName { get; set; }

        /// <summary>
        /// One Set function effect (stat = value) applied to <see cref="Character"/>, in its own layout; when set,
        /// <see cref="NanoEffects"/> and <see cref="NanoName"/> are not written. Serialized by SpellListMessageSerializer.
        /// </summary>
        public SpellListSetStat SetStat { get; set; }

        /// <summary>
        /// One GfxEffect function effect (a gfx played on <see cref="Character"/>), in its own layout; when set, nothing
        /// else is written. Serialized by SpellListMessageSerializer.
        /// </summary>
        public SpellListGfxEffect GfxEffect { get; set; }

        #endregion
    }

    /// <summary>
    /// A GfxEffect function effect in a SpellList: the Fresh Recruit hatch plays gfx 43232 on its NPC this way about
    /// 0.26 s after it spawns (capture 2026-11-09 22:50, Alien Mothership). <see cref="Effect"/> is the function type
    /// (0xCF26, GfxEffect) and a per-effect instance.
    /// </summary>
    public sealed class SpellListGfxEffect
    {
        public Identity Effect { get; set; }

        public int Gfx { get; set; }
    }

    /// <summary>
    /// A Set function effect in a SpellList: the Fresh Recruit hatch grows its NPC by Scale (stat 360) 31, 61, 81 this way
    /// (capture 2026-11-09 22:50, Alien Mothership). <see cref="Effect"/> is the function type (0xCF22, Set) and a
    /// per-effect instance.
    /// </summary>
    public sealed class SpellListSetStat
    {
        public Identity Effect { get; set; }

        public int Stat { get; set; }

        public int Value { get; set; }
    }
}