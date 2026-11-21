using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShortcutHookCore.Models;

namespace ShortcutHookCore.Security
{
    public static class DpapiHelper
    {
        public static readonly JsonSerializerOptions SignJsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static string Encrypt(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return "";
            try
            {
                byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
                byte[] encryptedBytes = ProtectedData.Protect(plaintextBytes, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encryptedBytes);
            }
            catch
            {
                return plaintext;
            }
        }

        public static string Decrypt(string encryptedBase64)
        {
            if (string.IsNullOrEmpty(encryptedBase64)) return "";
            try
            {
                byte[] encryptedBytes = Convert.FromBase64String(encryptedBase64);
                byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch
            {
                return encryptedBase64;
            }
        }

        private static byte[] GenerateSecureKey()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                byte[] key = new byte[32]; // 256 bits
                rng.GetBytes(key);
                return key;
            }
        }

        public static byte[] GetOrCreateHmacKey(string rootDir)
        {
            string keyPath = Path.Combine(rootDir, "shortcuts.key");
            try
            {
                if (File.Exists(keyPath))
                {
                    string base64Enc = File.ReadAllText(keyPath);
                    byte[] encryptedBytes = Convert.FromBase64String(base64Enc);
                    return ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                }
            }
            catch
            {
                // Fallback to generating a new key if reading/decrypting fails
            }

            byte[] newKey = GenerateSecureKey();
            try
            {
                byte[] encryptedBytes = ProtectedData.Protect(newKey, null, DataProtectionScope.CurrentUser);
                string base64Enc = Convert.ToBase64String(encryptedBytes);
                Directory.CreateDirectory(rootDir);
                File.WriteAllText(keyPath, base64Enc);
            }
            catch { }
            return newKey;
        }

        public static string ComputeHmac(string json, byte[] key)
        {
            using (var hmac = new HMACSHA256(key))
            {
                byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
                byte[] hashBytes = hmac.ComputeHash(jsonBytes);
                return Convert.ToBase64String(hashBytes);
            }
        }

        public static void SignConfig(ConfigRoot config, string rootDir)
        {
            config.signature = null;
            byte[] key = GetOrCreateHmacKey(rootDir);
            string jsonWithoutSig = JsonSerializer.Serialize(config, SignJsonOpts);
            config.signature = ComputeHmac(jsonWithoutSig, key);
        }

        public static bool VerifyConfig(ConfigRoot config, string rootDir)
        {
            if (string.IsNullOrEmpty(config.signature)) return false;
            string savedSig = config.signature;
            config.signature = null;
            try
            {
                byte[] key = GetOrCreateHmacKey(rootDir);
                string jsonWithoutSig = JsonSerializer.Serialize(config, SignJsonOpts);
                string computedSig = ComputeHmac(jsonWithoutSig, key);
                return string.Equals(savedSig, computedSig, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
            finally
            {
                config.signature = savedSig;
            }
        }
    }
}
