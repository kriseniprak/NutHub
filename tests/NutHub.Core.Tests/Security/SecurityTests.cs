using System.Net;
using System.Security.Cryptography;
using NutHub.Core.Security;
using NutHub.Core.Tests.Support;

namespace NutHub.Core.Tests.Security;

public sealed class PasswordHasherTests
{
    // The minimum accepted iteration count keeps the tests fast; the format is the same.
    private readonly Pbkdf2PasswordHasher _hasher = new(iterations: 1_000);

    [Fact]
    public void A_hash_verifies_its_password_only()
    {
        string hash = _hasher.Hash("correct horse");
        Assert.StartsWith("pbkdf2-sha256$1000$", hash, StringComparison.Ordinal);
        Assert.True(_hasher.Verify("correct horse", hash));
        Assert.False(_hasher.Verify("correct Horse", hash));
        Assert.False(_hasher.Verify("", hash));
    }

    [Fact]
    public void The_same_password_gets_a_different_salt_each_time()
    {
        string a = _hasher.Hash("secret");
        string b = _hasher.Hash("secret");
        Assert.NotEqual(a, b);
        Assert.True(_hasher.Verify("secret", a));
        Assert.True(_hasher.Verify("secret", b));
    }

    [Fact]
    public void Hashes_from_another_iteration_count_still_verify()
    {
        string hash = new Pbkdf2PasswordHasher(2_000).Hash("pw");
        Assert.True(_hasher.Verify("pw", hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain-text-password")]
    [InlineData("pbkdf2-sha256$1000$salt")]
    [InlineData("md5$1000$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$abc$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$999$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$99999999$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$1000$not base64!$aGFzaA==")]
    [InlineData("pbkdf2-sha256$1000$c2FsdA==$")]
    public void Malformed_hashes_never_verify_nor_throw(string? hash) =>
        Assert.False(_hasher.Verify("pw", hash));

    [Fact]
    public void Null_password_does_not_verify()
    {
        string hash = _hasher.Hash("pw");
        Assert.False(_hasher.Verify(null!, hash));
        Assert.Throws<ArgumentNullException>(() => _hasher.Hash(null!));
    }

    [Fact]
    public void The_verification_cache_never_accepts_a_wrong_password()
    {
        string hash = _hasher.Hash("pw");
        Assert.True(_hasher.Verify("pw", hash));
        Assert.True(_hasher.Verify("pw", hash)); // cached
        Assert.False(_hasher.Verify("pw2", hash));
        string other = _hasher.Hash("other");
        Assert.False(_hasher.Verify("pw", other));
    }

    [Fact]
    public void The_cache_survives_being_filled_up()
    {
        string hash = _hasher.Hash("pw");
        for (int i = 0; i < 300; i++)
        {
            Assert.True(_hasher.Verify("pw", hash) && !_hasher.Verify("x" + i, hash));
            _hasher.Verify("pw" + i, _hasher.Hash("pw" + i));
        }

        Assert.True(_hasher.Verify("pw", hash));
    }

    [Fact]
    public void Generated_passwords_use_an_unambiguous_alphabet()
    {
        string password = Pbkdf2PasswordHasher.GeneratePassword();
        Assert.Equal(16, password.Length);
        Assert.DoesNotContain(password, c => "0O1lI".Contains(c));
        Assert.Equal(30, Pbkdf2PasswordHasher.GeneratePassword(30).Length);
        Assert.NotEqual(Pbkdf2PasswordHasher.GeneratePassword(), Pbkdf2PasswordHasher.GeneratePassword());
    }
}

public sealed class SecretProtectorTests
{
    private readonly AesSecretProtector _protector = new(RandomNumberGenerator.GetBytes(32));

    [Theory]
    [InlineData("s3cret")]
    [InlineData("àèìòù € 漢字 with spaces")]
    [InlineData("q#7")]
    public void Round_trip(string secret)
    {
        string protectedValue = _protector.Protect(secret)!;
        Assert.StartsWith("enc:v1:", protectedValue, StringComparison.Ordinal);
        Assert.True(_protector.IsProtected(protectedValue));
        Assert.DoesNotContain(secret, protectedValue, StringComparison.Ordinal);
        Assert.Equal(secret, _protector.Unprotect(protectedValue));
    }

    [Fact]
    public void Null_empty_and_already_protected_values_are_left_alone()
    {
        Assert.Null(_protector.Protect(null));
        Assert.Equal("", _protector.Protect(""));
        string once = _protector.Protect("a")!;
        Assert.Equal(once, _protector.Protect(once));
        Assert.Equal("clear", _protector.Unprotect("clear"));
        Assert.Null(_protector.Unprotect(null));
        Assert.False(_protector.IsProtected(null));
    }

    [Fact]
    public void Each_protection_uses_a_new_nonce() =>
        Assert.NotEqual(_protector.Protect("same"), _protector.Protect("same"));

    [Fact]
    public void Tampered_values_are_rejected()
    {
        string value = _protector.Protect("secret")!;
        byte[] blob = Convert.FromBase64String(value["enc:v1:".Length..]);
        blob[^1] ^= 0x01;
        string tampered = "enc:v1:" + Convert.ToBase64String(blob);
        Assert.ThrowsAny<CryptographicException>(() => _protector.Unprotect(tampered));
    }

    [Fact]
    public void Another_key_cannot_decrypt()
    {
        string value = _protector.Protect("secret")!;
        var other = new AesSecretProtector(RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(value));
    }

    [Theory]
    [InlineData("enc:v1:not base64!")]
    [InlineData("enc:v1:AAAA")]
    public void Corrupt_values_throw_a_cryptographic_exception(string value) =>
        Assert.ThrowsAny<CryptographicException>(() => _protector.Unprotect(value));

    [Fact]
    public void Keys_must_be_256_bits() =>
        Assert.Throws<ArgumentException>(() => new AesSecretProtector(new byte[16]));

    [Fact]
    public void LoadOrCreate_keeps_the_key_between_runs()
    {
        using var dir = new TempDirectory();
        string keyFile = dir.File("keys/secret.key");
        string value = AesSecretProtector.LoadOrCreate(keyFile).Protect("persisted")!;
        Assert.True(File.Exists(keyFile));
        Assert.Equal("persisted", AesSecretProtector.LoadOrCreate(keyFile).Unprotect(value));
    }
}

public sealed class AddressFilterTests
{
    [Fact]
    public void An_empty_filter_allows_everyone()
    {
        Assert.True(AddressFilter.AllowAll.IsAllowed(IPAddress.Parse("8.8.8.8")));
        Assert.True(AddressFilter.Parse(null).IsEmpty);
        Assert.True(AddressFilter.Parse(["", "  "]).IsAllowed(null));
    }

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.77", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("10.0.0.0/8", "10.255.255.255", true)]
    [InlineData("192.168.1.10", "192.168.1.10", true)]
    [InlineData("192.168.1.10", "192.168.1.11", false)]
    [InlineData("fd00::/8", "fd12:3456::1", true)]
    [InlineData("fd00::/8", "fe80::1", false)]
    [InlineData("::1", "::1", true)]
    [InlineData(" 192.168.1.0/24 ", "192.168.1.5", true)]
    public void Networks_and_single_addresses(string entry, string address, bool allowed) =>
        Assert.Equal(allowed, AddressFilter.Parse([entry]).IsAllowed(IPAddress.Parse(address)));

    [Fact]
    public void IPv4_mapped_IPv6_clients_match_IPv4_entries()
    {
        AddressFilter filter = AddressFilter.Parse(["192.168.1.0/24"]);
        Assert.True(filter.IsAllowed(IPAddress.Parse("::ffff:192.168.1.20")));
        Assert.False(filter.IsAllowed(IPAddress.Parse("::ffff:192.168.9.20")));
        Assert.True(AddressFilter.Parse(["::ffff:10.1.2.3"]).IsAllowed(IPAddress.Parse("10.1.2.3")));
    }

    [Fact]
    public void Address_families_never_mix()
    {
        AddressFilter filter = AddressFilter.Parse(["0.0.0.0/0"]);
        Assert.True(filter.IsAllowed(IPAddress.Parse("203.0.113.9")));
        Assert.False(filter.IsAllowed(IPAddress.Parse("2001:db8::1")));
        Assert.False(filter.IsAllowed(null));
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("192.168.1.0/33")]
    [InlineData("192.168.1/24x")]
    public void Invalid_entries_are_reported(string entry)
    {
        Assert.False(AddressFilter.TryParseNetwork(entry, out _));
        Assert.Throws<FormatException>(() => AddressFilter.Parse([entry]));
    }
}
