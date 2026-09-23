using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Security;
using NutHub.Core.Drivers;

namespace NutHub.Drivers.Snmp.Transport;

/// <summary>
/// Builds the SharpSnmpLib providers for an SNMPv3 user. Kept separate from the transport so the configuration can
/// be checked (and tested) without a device: an unsupported algorithm becomes a configuration error naming the option.
/// </summary>
internal static class SnmpSecurity
{
    /// <summary>
    /// The privacy provider (which carries the authentication provider) for the configured security level.
    /// </summary>
    public static IPrivacyProvider CreatePrivacyProvider(SnmpSettings settings)
    {
        if (settings.SecurityLevel == SnmpSecurityLevel.NoAuthNoPriv)
        {
            return DefaultPrivacyProvider.DefaultPair;
        }

        IAuthenticationProvider auth = CreateAuthenticationProvider(settings.AuthProtocol, settings.AuthPassword);
        if (settings.SecurityLevel == SnmpSecurityLevel.AuthNoPriv)
        {
            return new DefaultPrivacyProvider(auth);
        }

        var phrase = new OctetString(settings.PrivPassword);

        // DES and MD5/SHA-1 are obsolete, yet many UPS network cards still offer nothing else.
#pragma warning disable CS0618
        switch (settings.PrivProtocol)
        {
            case SnmpPrivProtocol.Des:
                if (!DESPrivacyProvider.IsSupported)
                {
                    throw new DriverConfigurationException(
                        "DES privacy is not available on this system (the cryptography library lacks DES); choose AES.",
                        "privProtocol");
                }

                return new DESPrivacyProvider(phrase, auth);
            case SnmpPrivProtocol.Aes128:
            case SnmpPrivProtocol.Aes192:
            case SnmpPrivProtocol.Aes256:
                if (!AESPrivacyProviderBase.IsSupported)
                {
                    throw new DriverConfigurationException("AES privacy is not available on this system.", "privProtocol");
                }

                return settings.PrivProtocol switch
                {
                    SnmpPrivProtocol.Aes128 => new AESPrivacyProvider(phrase, auth),
                    SnmpPrivProtocol.Aes192 => new AES192PrivacyProvider(phrase, auth),
                    _ => new AES256PrivacyProvider(phrase, auth),
                };
            default:
                throw new DriverConfigurationException("Unknown privacy protocol.", "privProtocol");
        }
#pragma warning restore CS0618
    }

    public static IAuthenticationProvider CreateAuthenticationProvider(SnmpAuthProtocol protocol, string password)
    {
        var phrase = new OctetString(password);
#pragma warning disable CS0618
        return protocol switch
        {
            SnmpAuthProtocol.Md5 => new MD5AuthenticationProvider(phrase),
            SnmpAuthProtocol.Sha1 => new SHA1AuthenticationProvider(phrase),
            SnmpAuthProtocol.Sha256 => new SHA256AuthenticationProvider(phrase),
            SnmpAuthProtocol.Sha384 => new SHA384AuthenticationProvider(phrase),
            SnmpAuthProtocol.Sha512 => new SHA512AuthenticationProvider(phrase),
            _ => throw new DriverConfigurationException("Unknown authentication protocol.", "authProtocol"),
        };
#pragma warning restore CS0618
    }

    /// <summary>The user table the transport needs to decrypt and verify the agent's answers.</summary>
    public static UserRegistry CreateUserRegistry(SnmpSettings settings, IPrivacyProvider privacy)
    {
        var users = new UserRegistry();
        if (settings.Version == SnmpProtocolVersion.V3)
        {
            users.Add(new OctetString(settings.SecurityName), privacy);
        }

        return users;
    }
}
