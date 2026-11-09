using System.Security.Cryptography;
using System.Text;

namespace EdgeArmBridge;

// Chromium OSCrypt on Linux, enough of it to check one blob.
// v10 and v11 blobs are AES-128-CBC. The key is PBKDF2-HMAC-SHA1 over the
// Safe Storage secret, salt 'saltysalt', 1 iteration, IV of 16 spaces.
// See findings/2026-11-08-profile-mirroring-investigation.md, findings 2 and 5.
public static class OscryptCheck
{
    public static bool TryDecrypt(byte[] blob, string safeStorageSecret, out string plainText)
    {
        plainText = "";
        if (blob.Length <= 3)
            return false;
        var prefix = Encoding.ASCII.GetString(blob, 0, 3);
        if (prefix is not ("v10" or "v11"))
            return false;

        var key = Rfc2898DeriveBytes.Pbkdf2(safeStorageSecret, Encoding.ASCII.GetBytes("saltysalt"), 1, HashAlgorithmName.SHA1, 16);

        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = Encoding.ASCII.GetBytes(new string(' ', 16));
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            var plain = decryptor.TransformFinalBlock(blob, 3, blob.Length - 3);
            plainText = Encoding.UTF8.GetString(plain);
            // A wrong key can survive padding by chance. Real passwords and
            // cookie values are printable text, so ask for that too.
            return plainText.Length > 0 && plainText.All(c => !char.IsControl(c) || c is '\t');
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
