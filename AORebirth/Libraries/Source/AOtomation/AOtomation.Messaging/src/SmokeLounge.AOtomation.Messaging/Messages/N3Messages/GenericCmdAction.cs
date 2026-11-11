// --------------------------------------------------------------------------------------------------------------------
// <copyright file="GenericCmdAction.cs" company="SmokeLounge">
//   Copyright © 2013 SmokeLounge.
//   This program is free software. It comes without any warranty, to
//   the extent permitted by applicable law. You can redistribute it
//   and/or modify it under the terms of the Do What The Fuck You Want
//   To Public License, Version 2, as published by Sam Hocevar. See
//   http://www.wtfpl.net/ for more details.
// </copyright>
// <summary>
//   Defines the GenericCmdAction type.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace SmokeLounge.AOtomation.Messaging.Messages.N3Messages
{
    public enum GenericCmdAction
    {
        None = 0, 

        Get = 1, 

        Drop = 2, 

        Use = 3, 

        Repair = 4, 

        UseItemOnItem = 5,

        /// <summary>An inventory item used on a character: Target is the item's slot, then the character (Gamecode N3Msg_UseItemOnCharacter 10026a2b).</summary>
        UseItemOnCharacter = 0x20
    }
}