#region License

// Copyright (c) 2005-2014, CellAO Team
// 
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
// 
//     * Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
//     * Neither the name of the CellAO Team nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
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

#endregion

namespace AORebirth.Core.Encryption
{
    #region Usings ...

    using System;
    using System.Security.Cryptography;

    #endregion

    /// <summary>
    /// </summary>
    public class PasswordHash
    {
        // Constants which describe the salt/hash and number of iterations
        // These can be changed without breaking the existing passwords
        /// <summary>
        /// Size of the salt
        /// </summary>
        public const int SaltSize = 30;

        /// <summary>
        /// Size of the hash
        /// </summary>
        public const int HashSize = 30;

        /// <summary>
        /// Minimum number of PBKDF2 iterations to use
        /// </summary>
        public const int MinPBKDF2Iterations = 1111;

        /// <summary>
        /// Work factor for new hashes. Each login pays this once on a network thread, so it is a balance; raising it
        /// upgrades stored hashes as players next log in.
        /// </summary>
        public const int Iterations = 200000;

        /// <summary>Marks a PBKDF2-HMAC-SHA256 hash. A hash without a prefix is the legacy PBKDF2-HMAC-SHA1 format.</summary>
        private const string Sha256Prefix = "sha256";

        // Bounds what a stored value may ask of the CPU.
        private const int MaxIterations = 10000000;

        private const int Sha256HashSize = 32;

        /// <summary>
        /// Creates a salted PBKDF2 hash of the password.
        /// </summary>
        /// <param name="password">
        /// Clear password
        /// </param>
        /// <returns>
        /// The hash of the password.
        /// </returns>
        public static string CreateHash(string password)
        {
            byte[] salt = new byte[SaltSize];
            using (RandomNumberGenerator csprng = RandomNumberGenerator.Create())
            {
                csprng.GetBytes(salt);
            }

            // sha256:iterations:salt:hash is 99 characters; login.Password holds 100.
            byte[] hash = PBKDF2(password, salt, Iterations, Sha256HashSize, HashAlgorithmName.SHA256);
            return Sha256Prefix + ":" + Iterations + ":" + Convert.ToBase64String(salt) + ":"
                + Convert.ToBase64String(hash);
        }

        /// <summary>
        /// True when a stored hash is weaker than what <see cref="CreateHash"/> produces now, so the caller should
        /// replace it once the password has been verified.
        /// </summary>
        public static bool NeedsRehash(string storedHash)
        {
            if (string.IsNullOrEmpty(storedHash))
            {
                return false;
            }

            string[] split = storedHash.Split(':');
            int iterations;
            return split.Length != 4 || split[0] != Sha256Prefix
                || !int.TryParse(split[1], out iterations) || iterations < Iterations;
        }

        /// <summary>
        /// Validates a password against a hash
        /// </summary>
        /// <param name="password">
        /// </param>
        /// <param name="correctHash">
        /// </param>
        /// <returns>
        /// True if the hash of the password is the same as correctHash. False otherwise.
        /// </returns>
        public static bool ValidatePassword(string password, string correctHash)
        {
            // Use the same iterations and salt as in correctHash
            char[] delimiter = { ':' };
            string[] split = correctHash.Split(delimiter);
            HashAlgorithmName algorithm = HashAlgorithmName.SHA1;
            int first = 0;
            if (split.Length == 4 && split[0] == Sha256Prefix)
            {
                algorithm = HashAlgorithmName.SHA256;
                first = 1;
            }
            else if (split.Length != 3)
            {
                throw new FormatException("Unrecognized password hash format.");
            }

            int iterations = int.Parse(split[first]);
            byte[] salt = Convert.FromBase64String(split[first + 1]);
            byte[] hash = Convert.FromBase64String(split[first + 2]);
            if (iterations < 1 || iterations > MaxIterations || salt.Length < 8 || hash.Length < 16 || hash.Length > 64)
            {
                throw new FormatException("Password hash parameters are out of range.");
            }

            byte[] testHash = PBKDF2(password, salt, iterations, hash.Length, algorithm);
            return SlowEquals(hash, testHash);
        }

        /// <summary>
        /// Speed attack proof comparision of byte arrays
        /// </summary>
        /// <param name="theOne">
        /// The first byte array.
        /// </param>
        /// <param name="theOther">
        /// The second byte array.
        /// </param>
        /// <returns>
        /// True if both byte arrays are equal. False otherwise.
        /// </returns>
        private static bool SlowEquals(byte[] theOne, byte[] theOther)
        {
            uint diff = (uint)theOne.Length ^ (uint)theOther.Length;
            for (int i = 0; i < theOne.Length && i < theOther.Length; i++)
            {
                diff |= (uint)(theOne[i] ^ theOther[i]);
            }

            return diff == 0;
        }

        /// <summary>
        /// Computes the PBKDF2-SHA1 hash of a password.
        /// </summary>
        /// <param name="password">
        /// The password to hash.
        /// </param>
        /// <param name="salt">
        /// The salt.
        /// </param>
        /// <param name="iterations">
        /// The PBKDF2 iteration count.
        /// </param>
        /// <param name="outputBytes">
        /// The length of the hash to generate, in bytes.
        /// </param>
        /// <returns>
        /// A hash of the password.
        /// </returns>
        private static byte[] PBKDF2(
            string password, byte[] salt, int iterations, int outputBytes, HashAlgorithmName algorithm)
        {
            using (Rfc2898DeriveBytes pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, algorithm))
            {
                return pbkdf2.GetBytes(outputBytes);
            }
        }
    }
}