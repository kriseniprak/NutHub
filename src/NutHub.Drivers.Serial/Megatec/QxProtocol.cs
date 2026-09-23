namespace NutHub.Drivers.Serial.Megatec;

/// <summary>
/// One dialect of the Q* family (a NUT nutdrv_qx "subdriver"): the table of values and instant commands, how the UPS is
/// recognised, and the limits of the delays it accepts. A new instance is made for every connection, because reading
/// the UPS can enable or disable items (as the NUT subdrivers do by clearing QX_FLAG_SKIP).
/// </summary>
internal abstract class QxProtocol
{
    protected QxProtocol(QxProtocolOptions options)
    {
        Options = options;
    }

    /// <summary>The protocol name as in NUT's "protocol" option: "megatec", "voltronic-qs"...</summary>
    public abstract string Name { get; }

    /// <summary>Published as driver.version.data, like nutdrv_qx does: "Megatec 0.10".</summary>
    public abstract string Version { get; }

    public abstract IReadOnlyList<QxField> Fields { get; }

    public abstract IReadOnlyList<QxCommand> Commands { get; }

    /// <summary>
    /// The value (cut out of the reply by the command's From/To) that means "command accepted"; any other non-empty
    /// reply means refused. Null when every non-empty reply is a refusal.
    /// </summary>
    public virtual string? Accepted => "ACK";

    /// <summary>The whole reply by which the UPS refuses a query or command, e.g. "(NAK\r"; null when none.</summary>
    public virtual string? Rejected => null;

    /// <summary>Accepted range of ups.delay.start, seconds.</summary>
    public virtual (int Min, int Max) OnDelayRange => (0, 599940);

    /// <summary>Accepted range of ups.delay.shutdown, seconds.</summary>
    public virtual (int Min, int Max) OffDelayRange => (12, 600);

    protected QxProtocolOptions Options { get; }

    /// <summary>
    /// The fields that must be read successfully for the UPS to be considered to speak this protocol, in order (NUT
    /// claim functions: status, then vendor or protocol information).
    /// </summary>
    public abstract IReadOnlyList<string> ClaimFields { get; }

    /// <summary>Whether a claim field that is well-formed but blank still identifies the protocol.</summary>
    public virtual bool ClaimAcceptsBlank(QxField field) => false;

    /// <summary>Rounds a requested ups.delay.start to what the protocol can express (whole minutes).</summary>
    public virtual int NormalizeOnDelay(int seconds) => seconds - (seconds % 60);

    /// <summary>Rounds a requested ups.delay.shutdown: tenths of a minute below one minute, whole minutes above.</summary>
    public virtual int NormalizeOffDelay(int seconds) => seconds < 60 ? seconds - (seconds % 6) : seconds - (seconds % 60);

    public QxField? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);

    public QxCommand? FindCommand(string name) =>
        Commands.FirstOrDefault(c => !c.Skip && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Applies the user's options to the table before the first poll (NUT blazer_initups): skip the rating or vendor
    /// queries, ignore the "Shutdown Active" bit.
    /// </summary>
    public virtual void ApplyOptions()
    {
        foreach (QxField field in Fields)
        {
            if (HonoursVendorOptions && Options.NoRating && field.Command == "F\r")
            {
                field.Skip = true;
            }

            if (HonoursVendorOptions && Options.NoVendor && field.Command is "I\r" or "FW?\r")
            {
                field.Skip = true;
            }

            if (Options.IgnoreShutdownActive && field.IsStatus && IsShutdownActiveBit(field))
            {
                field.Skip = true;
            }
        }
    }

    /// <summary>
    /// Whether the noRating / noVendor options apply (NUT: the protocols using blazer_makevartable rather than the
    /// "light" variant).
    /// </summary>
    protected virtual bool HonoursVendorOptions => false;

    /// <summary>Whether a status field is the "Shutdown Active" bit that ignoreSab turns off.</summary>
    protected virtual bool IsShutdownActiveBit(QxField field) => field.From == 44 && field.To == 44;
}
