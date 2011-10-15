﻿/*
 * Copyright (C) 2011 mooege project
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Mooege.Common.Extensions;
using Mooege.Core.MooNet.Accounts;

namespace Mooege.Core.MooNet.Authentication
{
    /// <summary>
    /// SRP6-a implementation.
    /// Specification: http://srp.stanford.edu/design.html
    /// </summary>
    public class SRP6a
    {
        // The following is a description of SRP-6 and 6a, the latest versions of SRP:
        // ---------------------------------------------------------------------------
        //   N    A large safe prime (N = 2q+1, where q is prime)
        //        All arithmetic is done modulo N.
        //   g    A generator modulo N
        //   k    Multiplier parameter (k = H(N, g) in SRP-6a, k = 3 for legacy SRP-6)
        //   s    User's salt
        //   I    Username
        //   p    Cleartext Password
        //   H()  One-way hash function
        //   ^    (Modular) Exponentiation
        //   u    Random scrambling parameter
        //   a,b  Secret ephemeral values
        //   A,B  Public ephemeral values
        //   x    Private key (derived from p and s)
        //   v    Password verifier
        // ---------------------------------------------------------------------------

        private static readonly SHA256Managed H = new SHA256Managed(); // H() One-way hash function.
        private readonly BigInteger s; // users's salt.
        private readonly BigInteger I; // username.
        private readonly BigInteger v; // Password verifier -  v = g^x
        private readonly BigInteger b; // server's secret ephemeral value.
        private readonly BigInteger B; // server's public ephemeral value

        public Account Account { get; private set; }
        public string AccountSalt { get; private set; }
        public string Email { get; private set; }

        /// <summary>
        /// command == 0
        /// byte accountSalt[32]; - static value per account (skipped when command == 1)
        /// byte passwordSalt[32]; - static value per account
        /// byte serverChallenge[128]; - changes every login
        /// byte secondChallenge[128]; - changes every login
        /// </summary>
        public byte[] LogonChallenge { get; private set; }

        /// <summary>
        /// command == 3
        /// byte M2[32];
        /// byte secondProof[128]; // for veryfing secondChallenge
        /// </summary>
        public byte[] LogonProof { get; private set; }

        private static readonly byte[] gBytes = new byte[] { 0x02 };
        private static readonly BigInteger g = gBytes.ToBigInteger();

        private static readonly byte[] NBytes = new byte[]
            {
                0xAB, 0x24, 0x43, 0x63, 0xA9, 0xC2, 0xA6, 0xC3, 0x3B, 0x37, 0xE4, 0x61, 0x84, 0x25, 0x9F, 0x8B,
                0x3F, 0xCB, 0x8A, 0x85, 0x27, 0xFC, 0x3D, 0x87, 0xBE, 0xA0, 0x54, 0xD2, 0x38, 0x5D, 0x12, 0xB7,
                0x61, 0x44, 0x2E, 0x83, 0xFA, 0xC2, 0x21, 0xD9, 0x10, 0x9F, 0xC1, 0x9F, 0xEA, 0x50, 0xE3, 0x09,
                0xA6, 0xE5, 0x5E, 0x23, 0xA7, 0x77, 0xEB, 0x00, 0xC7, 0xBA, 0xBF, 0xF8, 0x55, 0x8A, 0x0E, 0x80,
                0x2B, 0x14, 0x1A, 0xA2, 0xD4, 0x43, 0xA9, 0xD4, 0xAF, 0xAD, 0xB5, 0xE1, 0xF5, 0xAC, 0xA6, 0x13,
                0x1C, 0x69, 0x78, 0x64, 0x0B, 0x7B, 0xAF, 0x9C, 0xC5, 0x50, 0x31, 0x8A, 0x23, 0x08, 0x01, 0xA1,
                0xF5, 0xFE, 0x31, 0x32, 0x7F, 0xE2, 0x05, 0x82, 0xD6, 0x0B, 0xED, 0x4D, 0x55, 0x32, 0x41, 0x94,
                0x29, 0x6F, 0x55, 0x7D, 0xE3, 0x0F, 0x77, 0x19, 0xE5, 0x6C, 0x30, 0xEB, 0xDE, 0xF6, 0xA7, 0x86
            };

        private static readonly BigInteger N = NBytes.ToBigInteger();
        
        private readonly BigInteger _secondChallenge;
        private BigInteger _secondProof;
        private BigInteger _secondChallengeClient;

        public SRP6a(string email, string password)
        {
            this.Email = email;
            this.AccountSalt = H.ComputeHash(Encoding.ASCII.GetBytes(email)).ToHexString();
            var sBytes = GetRandomBytes(32);
            this.s = sBytes.ToBigInteger();

            var IBytes = H.ComputeHash(Encoding.ASCII.GetBytes(this.AccountSalt.ToUpper() + ":" + password.ToUpper()));
            this.I = IBytes.ToBigInteger();

            var xBytes = H.ComputeHash(new byte[0].Concat(sBytes).Concat(IBytes).ToArray());
            var x = xBytes.ToBigInteger();

            this.v = BigInteger.ModPow(g, x, N);
            this.b = GetRandomBytes(128).ToBigInteger();

            var gMod = BigInteger.ModPow(g, b, N);

            var kBytes = H.ComputeHash(new byte[0].Concat(NBytes).Concat(gBytes).ToArray());
            var k = kBytes.ToBigInteger();

            this.B = BigInteger.Remainder((v * k) + gMod, N);

            this._secondChallenge = this.GetSecondChallenge();

            this.LogonChallenge = new byte[0]
                .Concat(new byte[] { 0 })
                .Concat(this.AccountSalt.ToByteArray()) // account-salt
                .Concat(sBytes) // password-salt
                .Concat(B.ToArray()) // server challenge
                .Concat(this._secondChallenge.ToArray()) // second challenge
                .ToArray();
        }

        public static byte[] CalculatePasswordVerifierForAccount(string email, string password, byte[] salt)
        {
            // x = H(s, p) -> s: randomly choosen salt, password: in plain-text.
            // v = g^x (computes password verifier)

            var x = H.ComputeHash(Encoding.ASCII.GetBytes(salt.ToString().ToUpper() + ":" + password.ToUpper())).ToBigInteger();
            var v = BigInteger.ModPow(g, x, N);

            return v.ToArray();
        }

        public static byte[] GetRandomBytes(int count)
        {
            var rnd = new Random();
            var result = new byte[count];
            rnd.NextBytes(result);
            return result;
        }

        public BigInteger GetSecondChallenge()
        {
            var bytes = new byte[]
            {
                0x5B, 0xE8, 0xF1, 0x95, 0x54, 0x3C, 0x1E, 0xD2, 0xA2, 0x2D, 0x84, 0x88, 0xB0, 0x60, 0xA3, 0x94, 
                0x23, 0x68, 0x65, 0xD5, 0x00, 0xEC, 0x62, 0x92, 0x95, 0x82, 0xEB, 0xA6, 0x31, 0xEB, 0xF5, 0x0E, 
                0xFD, 0x1E, 0x14, 0x8E, 0x9C, 0x55, 0x9C, 0x62, 0x4B, 0x31, 0x72, 0xE8, 0x2E, 0xD4, 0xC2, 0x5D, 
                0x0A, 0x96, 0xF1, 0xA5, 0xFD, 0xE8, 0x04, 0xDA, 0xBE, 0x23, 0x72, 0x97, 0x09, 0xA6, 0xB2, 0x92, 
                0xD3, 0x67, 0xFF, 0xD8, 0x20, 0xC5, 0xCB, 0xC8, 0xF4, 0x8D, 0x16, 0xD7, 0xD0, 0x12, 0xF8, 0x48, 
                0xD1, 0x05, 0xAE, 0x03, 0xBA, 0x58, 0x49, 0x9C, 0x8A, 0xB7, 0x56, 0xAA, 0xC8, 0xFB, 0x18, 0x5E, 
                0x7E, 0x4E, 0x1B, 0x2C, 0xD0, 0x4C, 0xDA, 0xA3, 0xB7, 0x52, 0xDD, 0x89, 0x14, 0xE2, 0x1E, 0x73, 
                0xA3, 0x98, 0x5D, 0x5A, 0x41, 0xE8, 0x01, 0xDA, 0x90, 0xCD, 0x61, 0x9D, 0x6E, 0xDD, 0x41, 0x68
            };

            var sc = bytes.ToBigInteger();
            return sc;
        }

        public BigInteger GetSecondProof(byte[] accountNameBytes, byte[] seed, byte[] secondChallenge)
        {
            byte[] spBytes = {
                0x7D, 0x95, 0x74, 0x0C, 0xAD, 0x32, 0x17, 0x1C, 0xBA, 0x75, 0x02, 0xB3, 0xA5, 0xD1, 0x00, 0x5A, 
                0x5A, 0x4C, 0x32, 0x3C, 0xD6, 0x3A, 0x94, 0xF2, 0x55, 0xDB, 0x05, 0x1E, 0x95, 0x30, 0x7D, 0xC2, 
                0x69, 0xB8, 0x64, 0x90, 0xE2, 0x79, 0xCA, 0xD7, 0x5D, 0x8D, 0x77, 0x51, 0x7E, 0xC7, 0x29, 0xB7, 
                0x03, 0x01, 0xB3, 0x62, 0xC4, 0x6D, 0xEA, 0x4F, 0xF5, 0x44, 0x6E, 0x9C, 0x05, 0x6F, 0x2C, 0x04, 
                0xCA, 0x96, 0x32, 0x77, 0x21, 0x29, 0xB8, 0x83, 0xE0, 0x13, 0x3B, 0x5C, 0x99, 0x82, 0x08, 0x7B, 
                0x63, 0xBF, 0x0D, 0xDA, 0xB7, 0x77, 0x63, 0xB4, 0xD1, 0xEF, 0x64, 0x60, 0x63, 0x5A, 0xBB, 0xDF, 
                0x5C, 0xA5, 0x1C, 0xC3, 0x60, 0xCE, 0x8F, 0xD6, 0xC4, 0x15, 0x55, 0xBB, 0x6D, 0x99, 0xD2, 0x26, 
                0x74, 0x1B, 0x4F, 0x2E, 0xE4, 0x42, 0x5C, 0xB5, 0x84, 0x44, 0x40, 0x60, 0xA7, 0xDD, 0x52, 0x18
            };

            var sp = spBytes.ToBigInteger();

            return sp;
        }

        public bool Verify(byte[] ABytes, byte[] M_client, byte[] seed)
        {
            this._secondChallengeClient = seed.ToBigInteger();
            var A = ABytes.ToBigInteger();

            var uBytes = H.ComputeHash(new byte[0].Concat(ABytes).Concat(B.ToArray()).ToArray());
            var u = uBytes.ToBigInteger();

            var S = BigInteger.ModPow(A * BigInteger.ModPow(v, u, N), b, N);

            var KBytes = Calc_K(S.ToArray());
            var t3Bytes = Hash_g_and_N_and_xor_them();

            var t4 = H.ComputeHash(Encoding.ASCII.GetBytes(this.AccountSalt));

            var sBytes = s.ToArray();
            var BBytes = B.ToArray();

            var M = H.ComputeHash(new byte[0]
                                            .Concat(t3Bytes)
                                            .Concat(t4)
                                            .Concat(sBytes)
                                            .Concat(ABytes)
                                            .Concat(BBytes)
                                            .Concat(KBytes)
                                            .ToArray());

            if (!M.CompareTo(M_client))
                return false;

            // proof of session key

            var M_server = H.ComputeHash(new byte[0]
                                             .Concat(ABytes)
                                             .Concat(M)
                                             .Concat(KBytes)
                                             .ToArray());

            this._secondProof = GetSecondProof(Encoding.ASCII.GetBytes(this.Email), seed,
                                               this._secondChallenge.ToArray());

            LogonProof = new byte[0]
                .Concat(new byte[] { 3 }) // command == 3 - server sends proof of session key to client
                .Concat(M_server) // proof of session key
                .Concat(this._secondProof.ToArray()) // second proof
                .ToArray();

            return true;
        }

        //  Interleave SHA256 Key
        private byte[] Calc_K(byte[] S)
        {
            var K = new byte[64];

            var half_S = new byte[64];

            for (int i = 0; i < 64; ++i)
                half_S[i] = S[i * 2];

            var p1 = H.ComputeHash(half_S);

            for (int i = 0; i < 32; ++i)
                K[i * 2] = p1[i];

            for (int i = 0; i < 64; ++i)
                half_S[i] = S[i * 2 + 1];

            var p2 = H.ComputeHash(half_S);

            for (int i = 0; i < 32; ++i)
                K[i * 2 + 1] = p2[i];

            return K;
        }

        private byte[] Hash_g_and_N_and_xor_them()
        {
            var hash_N = H.ComputeHash(NBytes);
            var hash_g = H.ComputeHash(gBytes);

            for (var i = 0; i < 32; ++i)
                hash_N[i] ^= hash_g[i];

            return hash_N;
        }
    }
}
