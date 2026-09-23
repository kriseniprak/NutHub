using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NutHub.Core.Security;

/// <summary>Hashes and verifies the passwords of web and NUT accounts.</summary>
public interface IPasswordHasher
{
    /// <summary>Returns a self-describing hash string ("pbkdf2-sha256$iterations$salt$hash").</summary>
    string Hash(string password);

    /// <summary>Verifies a password in constant time. False for an empty or malformed hash.</summary>
    bool Verify(string password, string? hash);
}

/// <summary>
/// PBKDF2-HMAC-SHA256 with a random 16-byte salt. Successful verifications are remembered (keyed by a SHA-256 of the
/// hash and password, never the password itself) so that upsmon reconnecting every few seconds does not cost a full
/// key derivation each time.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    public const int DefaultIterations = 150_000;
    private const string Prefix = "pbkdf2-sha256";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int CacheLimit = 256;

    private readonly int _iterations;
    private readonly ConcurrentDictionary<string, byte> _verified = new(StringComparer.Ordinal);

    public Pbkdf2PasswordHasher(int iterations = DefaultIterations)
    {
        _iterations = iterations;
    }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, _iterations,
                                               HashAlgorithmName.SHA256, HashSize);
        return $"{Prefix}${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string? hash)
    {
        if (string.IsNullOrEmpty(hash) || password is null)
        {
            return false;
        }

        string cacheKey = CacheKey(password, hash);
        if (_verified.ContainsKey(cacheKey))
        {
            return true;
        }

        string[] parts = hash.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out int iterations) ||
            iterations < 1_000 || iterations > 10_000_000)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (expected.Length == 0)
        {
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
                                                 HashAlgorithmName.SHA256, expected.Length);
        bool ok = CryptographicOperations.FixedTimeEquals(actual, expected);
        if (ok)
        {
            if (_verified.Count >= CacheLimit)
            {
                _verified.Clear();
            }

            _verified.TryAdd(cacheKey, 0);
        }

        return ok;
    }

    private static string CacheKey(string password, string hash)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(hash + "\0" + password));
        return Convert.ToBase64String(digest);
    }

    /// <summary>A random password of URL-safe characters, for generated accounts.</summary>
    public static string GeneratePassword(int length = 16)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, length);
    }
}
