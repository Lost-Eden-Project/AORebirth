#region License

// Copyright (c) 2005-2014, CellAO Team
// 
// 
// All rights reserved.
// 
// 
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
// 
// 
//     * Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
//     * Neither the name of the CellAO Team nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
// 
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
// CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
// EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
// PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
// PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
// LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
// NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

#endregion

namespace SmokeLounge.AOtomation.Messaging.Messages.N3Messages
{
    #region Usings ...

    using System;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

    #endregion

    [AoContract((int)N3MessageType.WeatherControl)]
    public class WeatherControlMessage : N3Message
    {
        public WeatherControlMessage()
        {
            this.N3MessageType = N3MessageType.WeatherControl;
        }

        // Gamecode.dll WeatherControlIIR_t: FromStream 0x100bf335, apply 0x100be467 builds a weather entry that is
        // queued on the environment controller; 0x100be69e ages it each frame and 0x100be794 weights it by the
        // local player's distance to Position. An entry with zero weight is removed.

        /// <summary>Seconds to fade in (entry weight elapsed / FadeIn).</summary>
        [AoMember(1)]
        public short FadeIn { get; set; }

        /// <summary>Seconds at full weight after the fade in. Zero means it starts fading out at once.</summary>
        [AoMember(2)]
        public int Duration { get; set; }

        /// <summary>Seconds to fade out after Duration; the entry is then deleted.</summary>
        [AoMember(3)]
        public short FadeOut { get; set; }

        /// <summary>
        /// Radius in metres around <see cref="Position"/>. Weight is (1 - (d / Range)^2), and zero at or beyond it,
        /// so a small range or a far Position makes the weather invisible.
        /// </summary>
        [AoMember(4)]
        public Single Range { get; set; }

        [AoMember(5)]
        public byte WeatherType { get; set; }

        [AoMember(6)]
        public byte WeatherIntensity { get; set; }

        [AoMember(7)]
        public byte Wind { get; set; }

        /// <summary>
        /// Read as a signed byte into the environment as a raw number (not / 100). The frame code divides it by 3.5,
        /// clamps to 0..1 and uses it to scale the rain sound. Meaning beyond that is unverified; 0 is safe.
        /// </summary>
        [AoMember(8)]
        public byte Clouds { get; set; }

        [AoMember(9)]
        public byte Thunderstrikes { get; set; }

        [AoMember(10)]
        public byte Tremors { get; set; }

        [AoMember(11)]
        public byte TremorPercentage { get; set; }

        [AoMember(12)]
        public byte ThunderstrikePercentage { get; set; }

        // Bytes 13-19 feed the environment frame (Gamecode.dll 0x100bea97): 13-15 / 255 go to
        // VisualAmbientLight_t::AddAmbientLight; 16-18 / 255 and 19 / 100 go to VisualFog_t::AddFog as colour and
        // density, blended across entries by that density. A non-zero density lays a fog of this colour over
        // whatever the weather type draws.

        /// <summary>Ambient light added while the entry is active, red / 255.</summary>
        [AoMember(13)]
        public byte AmbientLightRed { get; set; }

        /// <summary>Ambient light added while the entry is active, green / 255.</summary>
        [AoMember(14)]
        public byte AmbientLightGreen { get; set; }

        /// <summary>Ambient light added while the entry is active, blue / 255.</summary>
        [AoMember(15)]
        public byte AmbientLightBlue { get; set; }

        /// <summary>Extra fog colour, red / 255; only drawn when <see cref="FogDensity"/> is non-zero.</summary>
        [AoMember(16)]
        public byte FogColorRed { get; set; }

        /// <summary>Extra fog colour, green / 255.</summary>
        [AoMember(17)]
        public byte FogColorGreen { get; set; }

        /// <summary>Extra fog colour, blue / 255.</summary>
        [AoMember(18)]
        public byte FogColorBlue { get; set; }

        /// <summary>Extra fog density, percent. 0 adds no fog of its own.</summary>
        [AoMember(19)]
        public byte FogDensity { get; set; }

        /// <summary>Centre of the weather in playfield coordinates; see <see cref="Range"/>.</summary>
        [AoMember(20)]
        public Vector3 Position { get; set; }
        /// <summary>The entry's starting age in seconds; 0 starts at the beginning of the fade in.</summary>
        [AoMember(21)]
        public Single UnknownSingle { get; set; }
    }
}