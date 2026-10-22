// --------------------------------------------------------------------------------------------------------------------
// <copyright file="GfxTriggerMessage.cs" company="SmokeLounge">
//   Copyright © 2013 SmokeLounge.
//   This program is free software. It comes without any warranty, to
//   the extent permitted by applicable law. You can redistribute it
//   and/or modify it under the terms of the Do What The Fuck You Want
//   To Public License, Version 2, as published by Sam Hocevar. See
//   http://www.wtfpl.net/ for more details.
// </copyright>
// <summary>
//   Defines the GfxTriggerMessage type.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace SmokeLounge.AOtomation.Messaging.Messages.N3Messages
{
    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Serialization;
    using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

    /// <summary>
    /// Server-to-client: play a gfx template. Gamecode.dll GfxTriggerIIR_t (read 0x10039879, write 0x10039937,
    /// apply 0x100399ea): after the N3 header, <see cref="Type"/> and <see cref="GfxId"/>, then a payload that
    /// depends on the type. The apply function resolves every identity with GetDynel and dynamic-casts it to
    /// SimpleChar_t, so character-anchored types only play on players and NPCs. The gfx template's category must
    /// match the anchor (0x3f2 character-anchored, 0x3f3 position-anchored) or the client builds no control.
    /// </summary>
    [AoContract((int)N3MessageType.GfxTrigger)]
    public class GfxTriggerMessage : N3Message
    {
        #region Constants

        /// <summary>Position only: effect at <see cref="Position"/>.</summary>
        public const int TypeAtPosition = 1;

        /// <summary>Character: effect on <see cref="Target"/>, with <see cref="Extra"/>.</summary>
        public const int TypeOnCharacter = 2;

        /// <summary>Two positions: effect from <see cref="Position"/> to <see cref="Position2"/>.</summary>
        public const int TypeBetweenPositions = 3;

        /// <summary>Character and position: effect at <see cref="Position"/>, bound to <see cref="Target"/>.</summary>
        public const int TypeOnCharacterAtPosition = 4;

        /// <summary>Character, position and extra.</summary>
        public const int TypeOnCharacterAtPositionExtra = 5;

        /// <summary>
        /// Two characters and extra. The client's apply function resolves <see cref="Target"/> for both ends
        /// (0x100399ea), so the effect is drawn on <see cref="Target"/> alone.
        /// </summary>
        public const int TypeBetweenCharacters = 6;

        /// <summary>The gfx id the client treats as "no effect" (CreateEffect2 returns 0 for it).</summary>
        public const int NoGfx = 49999;

        #endregion

        #region Constructors and Destructors

        public GfxTriggerMessage()
        {
            this.N3MessageType = N3MessageType.GfxTrigger;
        }

        #endregion

        #region AoMember Properties

        [AoMember(0)]
        [AoFlags("gfxType")]
        public int Type { get; set; }

        [AoMember(1)]
        public int GfxId { get; set; }

        [AoMember(2)]
        [AoUsesFlags("gfxType", typeof(Identity), FlagsCriteria.EqualsToAny, new[] { 2, 4, 5, 6 })]
        public Identity Target { get; set; }

        [AoMember(3)]
        [AoUsesFlags("gfxType", typeof(Identity), FlagsCriteria.EqualsToAny, new[] { 6 })]
        public Identity SecondTarget { get; set; }

        [AoMember(4)]
        [AoUsesFlags("gfxType", typeof(Vector3), FlagsCriteria.EqualsToAny, new[] { 1, 3, 4, 5 })]
        public Vector3 Position { get; set; }

        [AoMember(5)]
        [AoUsesFlags("gfxType", typeof(Vector3), FlagsCriteria.EqualsToAny, new[] { 3 })]
        public Vector3 Position2 { get; set; }

        /// <summary>Passed straight to CreateGfxControl; every client-internal caller passes 0.</summary>
        [AoMember(6)]
        [AoUsesFlags("gfxType", typeof(int), FlagsCriteria.EqualsToAny, new[] { 2, 5, 6 })]
        public int Extra { get; set; }

        #endregion
    }
}
