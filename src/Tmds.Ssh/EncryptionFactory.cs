// This file is part of Tmds.Ssh which is released under MIT.
// See file LICENSE for full license details.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Tmds.Ssh
{
    sealed class EncryptionFactory
    {
        class EncryptionInfo
        {
            public EncryptionInfo(int keyLength, int ivLength, Func<Name, byte[], byte[], bool, IDisposableCryptoTransform> create)
            {
                KeyLength = keyLength;
                IVLength = ivLength;
                Create = create;
            }

            public int KeyLength { get; }
            public int IVLength { get; }
            public Func<Name, byte[], byte[], bool, IDisposableCryptoTransform> Create { get; }
        }

        private readonly Dictionary<Name, EncryptionInfo> _algorithms;

        public static EncryptionFactory Default = new EncryptionFactory();

        public EncryptionFactory()
        {
            _algorithms = new Dictionary<Name, EncryptionInfo>();
            _algorithms.Add(AlgorithmNames.Aes256Cbc, new EncryptionInfo(keyLength: 256 / 8, ivLength: 128 / 8, CreateAes));
        }

        public IDisposableCryptoTransform CreateDecryptor(Name name, byte[] key, byte[] iv)
        {
            EncryptionInfo info = _algorithms[name];
            CheckLengths(info, key, iv);
            return info.Create(name, key, iv, false);
        }

        public IDisposableCryptoTransform CreateEncryptor(Name name, byte[] key, byte[] iv)
        {
            EncryptionInfo info = _algorithms[name];
            CheckLengths(info, key, iv);
            return info.Create(name, key, iv, true);
        }

        private void CheckLengths(EncryptionInfo info, byte[] key, byte[] iv)
        {
            if (info.IVLength != iv.Length)
            {
                throw new ArgumentException(nameof(iv));
            }
            if (info.KeyLength != key.Length)
            {
                throw new ArgumentException(nameof(key));
            }
        }

        public void GetKeyAndIVLength(Name name, out int keyLength, out int ivLength)
        {
            EncryptionInfo info = _algorithms[name];
            keyLength = info.KeyLength;
            ivLength = info.IVLength;
        }

        private static IDisposableCryptoTransform CreateAes(Name name, byte[] key, byte[] iv, bool encryptorNotDecryptor)
        {
            if (name == AlgorithmNames.Aes256Cbc)
            {
                var aes = Aes.Create();
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                aes.Key = key;
                aes.IV = iv;
                ICryptoTransform transform = encryptorNotDecryptor ? aes.CreateEncryptor() : aes.CreateDecryptor();
                return new EncryptionCryptoTransform(aes, transform, encryptorNotDecryptor);
            }
            else
            {
                throw new ArgumentException(nameof(name));
            }
        }
    }
}