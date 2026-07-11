using System;
using System.Security.Cryptography;
using System.Text;

namespace ShortcutHookCore.Security
{
    public static class DpapiHelper
    {
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
    }
}
