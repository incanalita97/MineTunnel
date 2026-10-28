using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MineTunnel.Core
{
    public static class CryptoSecurity
    {
        private static readonly RNGCryptoServiceProvider Rng = new RNGCryptoServiceProvider();

        public static byte[] GenerateRandomBytes(int length)
        {
            byte[] bytes = new byte[length];
            Rng.GetBytes(bytes);
            return bytes;
        }

        public static string GenerateRandomToken(int byteLength = 16)
        {
            byte[] bytes = GenerateRandomBytes(byteLength);
            // Convert to clean hex string
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        public static void DeriveKeys(byte[] masterSecret, out byte[] encKey, out byte[] macKey)
        {
            if (masterSecret == null || masterSecret.Length == 0)
            {
                throw new ArgumentException("Master secret cannot be empty.");
            }

            using (HMACSHA256 hmac = new HMACSHA256(masterSecret))
            {
                encKey = hmac.ComputeHash(Encoding.UTF8.GetBytes("MINETUNNEL_AES256_ENC_KEY_V1"));
                macKey = hmac.ComputeHash(Encoding.UTF8.GetBytes("MINETUNNEL_HMAC256_MAC_KEY_V1"));
            }
        }

        public static byte[] EncryptThenMac(byte[] plaintext, byte[] encKey, byte[] macKey)
        {
            if (plaintext == null) plaintext = new byte[0];

            byte[] iv = GenerateRandomBytes(16);
            byte[] ciphertext;

            using (AesManaged aes = new AesManaged())
            {
                aes.KeySize = 256;
                aes.Key = encKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (MemoryStream ms = new MemoryStream())
                {
                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(plaintext, 0, plaintext.Length);
                        cs.FlushFinalBlock();
                    }
                    ciphertext = ms.ToArray();
                }
            }

            // Compute HMAC over (IV + Ciphertext)
            byte[] hmacValue;
            using (HMACSHA256 hmac = new HMACSHA256(macKey))
            {
                hmac.TransformBlock(iv, 0, iv.Length, iv, 0);
                hmac.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                hmacValue = hmac.Hash;
            }

            // Pack: [ IV (16B) ] [ Ciphertext (N bytes) ] [ HMAC (32B) ]
            byte[] result = new byte[iv.Length + ciphertext.Length + hmacValue.Length];
            Array.Copy(iv, 0, result, 0, iv.Length);
            Array.Copy(ciphertext, 0, result, iv.Length, ciphertext.Length);
            Array.Copy(hmacValue, 0, result, iv.Length + ciphertext.Length, hmacValue.Length);

            return result;
        }

        public static byte[] VerifyThenDecrypt(byte[] packet, byte[] encKey, byte[] macKey)
        {
            // Minimum size: 16 (IV) + 16 (min PKCS7 block) + 32 (HMAC) = 64 bytes
            if (packet == null || packet.Length < 64)
            {
                return null;
            }

            int ivLen = 16;
            int hmacLen = 32;
            int cipherLen = packet.Length - ivLen - hmacLen;

            byte[] iv = new byte[ivLen];
            Array.Copy(packet, 0, iv, 0, ivLen);

            byte[] ciphertext = new byte[cipherLen];
            Array.Copy(packet, ivLen, ciphertext, 0, cipherLen);

            byte[] receivedHmac = new byte[hmacLen];
            Array.Copy(packet, ivLen + cipherLen, receivedHmac, 0, hmacLen);

            // 1. Strict HMAC verification before ANY decryption attempt
            byte[] expectedHmac;
            using (HMACSHA256 hmac = new HMACSHA256(macKey))
            {
                hmac.TransformBlock(iv, 0, iv.Length, iv, 0);
                hmac.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                expectedHmac = hmac.Hash;
            }

            if (!FixedTimeEquals(expectedHmac, receivedHmac))
            {
                // Discard immediately without decrypting
                return null;
            }

            // 2. Decrypt ciphertext
            try
            {
                using (AesManaged aes = new AesManaged())
                {
                    aes.KeySize = 256;
                    aes.Key = encKey;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                        {
                            cs.Write(ciphertext, 0, ciphertext.Length);
                            cs.FlushFinalBlock();
                        }
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        public static string ProtectSecret(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                return "DPAPI:" + Convert.ToBase64String(protectedBytes);
            }
            catch
            {
                return plainText;
            }
        }

        public static string UnprotectSecret(string storedText)
        {
            if (string.IsNullOrEmpty(storedText)) return "";
            if (!storedText.StartsWith("DPAPI:", StringComparison.OrdinalIgnoreCase))
            {
                // Unencrypted legacy fallback
                return storedText;
            }

            try
            {
                string base64 = storedText.Substring(6);
                byte[] protectedBytes = Convert.FromBase64String(base64);
                byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return "";
            }
        }
    }
}
