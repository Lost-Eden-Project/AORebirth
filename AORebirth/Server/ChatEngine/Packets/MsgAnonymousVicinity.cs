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

namespace ChatEngine.Packets
{
    /// <summary>
    /// The msg anonymous vicinity.
    /// </summary>
    public static class MsgAnonymousVicinity
    {
        #region Public Methods and Operators

        /// <summary>
        /// The create.
        /// </summary>
        /// <param name="arg1">
        /// </param>
        /// <param name="msg">
        /// </param>
        /// <param name="blob">
        /// </param>
        /// <returns>
        /// </returns>
        public static byte[] Create(string arg1, string msg, string blob)
        {
            // Live writes an empty name and blob as zero-length strings (0000). PacketWriter.WriteString writes "" as one
            // NUL byte (0001 00), which the client takes as a name and shows ": text".
            PacketWriter writer = new PacketWriter(35);
            WriteString(writer, arg1);
            WriteString(writer, msg);
            WriteString(writer, blob);
            return writer.Finish();
        }

        /// <summary>
        /// Same, with a raw blob: live sends one byte telling the client what the line is (04 a pet's answer, 06 a loot
        /// announcement; none for loot rights).
        /// </summary>
        public static byte[] Create(string name, string msg, byte[] blob)
        {
            PacketWriter writer = new PacketWriter(35);
            WriteString(writer, name);
            WriteString(writer, msg);
            writer.WriteUInt16((ushort)(blob?.Length ?? 0));
            if (blob != null && blob.Length > 0)
                writer.WriteBytes(blob);
            return writer.Finish();
        }

        private static void WriteString(PacketWriter writer, string value)
        {
            if (string.IsNullOrEmpty(value))
                writer.WriteUInt16(0);
            else
                writer.WriteString(value);
        }

        #endregion
    }
}