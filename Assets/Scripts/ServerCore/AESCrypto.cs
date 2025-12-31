using System;
using System.Security.Cryptography;

namespace ServerCore
{
    public class AESCrypto
    {
        private byte[] _key;
        private byte[] _iv;
        private bool _initialized = false;

        // 서버와 동일한 키
        public static readonly byte[] DefaultKey = new byte[]
        {
            0x4E, 0x61, 0x6D, 0x6F, 0x53, 0x65, 0x72, 0x76,  // "NamoServ"
            0x65, 0x72, 0x4B, 0x65, 0x79, 0x31, 0x32, 0x33   // "erKey123"
        };

        public bool Init(byte[] key)
        {
            if (key == null || key.Length != 16)
                return false;

            _key = new byte[16];
            _iv = new byte[16];
            Array.Copy(key, _key, 16);
            Array.Copy(key, _iv, 16);  // 서버와 동일하게 키를 IV로 사용
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

        public static int GetEncryptedSize(int plainSize)
        {
            return ((plainSize / 16) + 1) * 16;
        }
    }
}
