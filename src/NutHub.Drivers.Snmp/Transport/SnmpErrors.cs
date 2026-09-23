using Lextm.SharpSnmpLib;

namespace NutHub.Drivers.Snmp.Transport;

/// <summary>The agent did not answer a request, even after the retries.</summary>
internal sealed class SnmpNoResponseException(string target, string? detail = null)
    : Exception(detail is null ? $"No answer from {target}." : $"No answer from {target} ({detail}).")
{
    public string Target { get; } = target;

    /// <summary>Why the answer is missing when the network said so (port closed); null for a plain timeout.</summary>
    public string? Detail { get; } = detail;
}

/// <summary>Why an SNMPv3 exchange was refused.</summary>
internal enum SnmpAuthFailure
{
    /// <summary>usmStatsUnknownUserNames: the agent has no such user.</summary>
    UnknownUser,

    /// <summary>usmStatsWrongDigests, or an answer whose digest does not verify: wrong password or algorithm.</summary>
    WrongDigest,

    /// <summary>usmStatsDecryptionErrors, or an answer that cannot be decrypted: wrong privacy password or algorithm.</summary>
    DecryptionError,

    /// <summary>usmStatsUnsupportedSecLevels: the user is not configured for the requested security level.</summary>
    UnsupportedSecurityLevel,

    /// <summary>usmStatsNotInTimeWindows even after synchronising the clock.</summary>
    NotInTimeWindow,

    /// <summary>usmStatsUnknownEngineIDs even after a new discovery.</summary>
    UnknownEngineId,
}

/// <summary>An SNMPv3 authentication or privacy failure, with a message that tells the user what to check.</summary>
internal sealed class SnmpAuthenticationException(SnmpAuthFailure failure, string message) : Exception(message)
{
    public SnmpAuthFailure Failure { get; } = failure;
}

/// <summary>The agent answered with an error status (noSuchName, notWritable, wrongValue...).</summary>
internal sealed class SnmpErrorStatusException(ErrorCode status, int index, string? oid)
    : Exception($"The agent answered {status}" + (oid is null ? "." : $" for {oid}."))
{
    public ErrorCode Status { get; } = status;

    /// <summary>1-based index of the variable at fault, 0 when the agent did not say.</summary>
    public int Index { get; } = index;

    public string? Oid { get; } = oid;
}

/// <summary>The agent answered something unusable (unexpected report, missing engine ID...).</summary>
internal sealed class SnmpProtocolException(string message) : Exception(message);

/// <summary>None of the supported MIBs, or not the requested one, is implemented by the agent.</summary>
internal sealed class SnmpMibNotFoundException(string message) : Exception(message);
