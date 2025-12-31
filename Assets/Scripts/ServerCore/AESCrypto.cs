using System;
using System.Security.Cryptography;

namespace ServerCore
{
    public class AESCrypto
    {
        private byte[] _key;
        private byte[] _iv;
        private bool _initialized = false;

        public const int HMAC_SIZE = 32;
        public const int BLOCK_SIZE = 16;

        public static readonly byte[] DefaultKey = new byte[]
        {
            0x4E, 0x61, 0x6D, 0x6F, 0x53, 0x65, 0x72, 0x76,
            0x65, 0x72, 0x4B, 0x65, 0x79, 0x31, 0x32, 0x33
        };

        public bool IsInitialized => _initialized;

        public bool Init(byte[] key)
        {
            if (key == null || key.Length != 16)
                return false;

            _key = new byte[16];
            _iv = new byte[16];
            Array.Copy(key, _key, 16);
            Array.Copy(key, _iv, 16);
            _initialized = true;
            return true;
        }

        public byte[] Encrypt(byte[] data)
        {
            if (!_initialized || data == null || data.Length == 0)
                return null;

            using (Aes aes = Aes.Create())
            {
                aes.Key = _key;
                aes.IV = _iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var encryptor = aes.CreateEncryptor())
                {
                    return encryptor.TransformFinalBlock(data, 0, data.Length);
                }
            }
        }

        public byte[] Decrypt(byte[] data)
        {
            if (!_initialized || data == null || data.Length == 0)
                return null;

            using (Aes aes = Aes.Create())
            {
                aes.Key = _key;
                aes.IV = _iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var decryptor = aes.CreateDecryptor())
                {
                    return decryptor.TransformFinalBlock(data, 0, data.Length);
                }
            }
        }

        public byte[] ComputeHMAC(byte[] data, int offset, int count)
        {
            if (!_initialized) return null;
            using (HMACSHA256 hmac = new HMACSHA256(_key))
            {
                return hmac.ComputeHash(data, offset, count);
            }
        }

        public bool VerifyHMAC(byte[] data, int dataOffset, int dataCount,
                               byte[] buffer, int hmacOffset)
        {
            if (!_initialized) return false;
            byte[] computed = ComputeHMAC(data, dataOffset, dataCount);

            int result = 0;
            for (int i = 0; i < HMAC_SIZE; i++)
                result |= computed[i] ^ buffer[hmacOffset + i];
            return result == 0;
        }

        public static int GetEncryptedSize(int plainSize)
        {
            return ((plainSize / 16) + 1) * 16;
        }
    }
}
