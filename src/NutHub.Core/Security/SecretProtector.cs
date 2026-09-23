using System.Security.Cryptography;
using System.Text;

namespace NutHub.Core.Security;

/// <summary>
/// Encrypts the secrets stored in the configuration file, so that the file alone (a backup, a copy pasted in a
/// support request) does not reveal them. The key lives in a separate file of the data directory.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts clear text; returns values that are already protected, and null / empty, unchanged.</summary>
    string? Protect(string? plainText);

    /// <summary>
    /// Decrypts a protected value. A value without the "enc:" prefix is returned as is, so secrets typed by hand in
    /// the file work (they get encrypted at the next save).
    /// </summary>
    string? Unprotect(string? value);

    bool IsProtected(string? value);
}

/// <summary>AES-256-GCM with a random key kept in <see cref="NutHubPaths.SecretKeyFile"/>.</summary>
public sealed class AesSecretProtector : ISecretProtector
{
    private const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesSecretProtector(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("The key must be 32 bytes.", nameof(key));
        }

        _key = key;
    }

    /// <summary>Loads the key file, creating it (readable by the service account only) on first use.</summary>
    public static AesSecretProtector LoadOrCreate(string keyFile)
    {
        if (File.Exists(keyFile))
        {
            string text = File.ReadAllText(keyFile).Trim();
            byte[] key = Convert.FromBase64String(text);
            return new AesSecretProtector(key);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
        byte[] newKey = RandomNumberGenerator.GetBytes(32);
        string tmp = keyFile + ".tmp";
        File.WriteAllText(tmp, Convert.ToBase64String(newKey));
        FilePermissions.TryRestrictFile(tmp);
        File.Move(tmp, keyFile, overwrite: true);
        return new AesSecretProtector(newKey);
    }

    public bool IsProtected(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText) || IsProtected(plainText))
        {
            return plainText;
        }

        byte[] plain = Encoding.UTF8.GetBytes(plainText);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        byte[] blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        return Prefix + Convert.ToBase64String(blob);
    }

    public string? Unprotect(string? value)
    {
        if (!IsProtected(value))
        {
            return value;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(value![Prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("A protected value in the configuration is corrupt.", ex);
        }

        if (blob.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("A protected value in the configuration is corrupt.");
        }

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        byte[] plain = new byte[cipher.Length];
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }

        return Encoding.UTF8.GetString(plain);
    }
}
