namespace NutHub.Core.Model;

/// <summary>The lifecycle state of the driver of one UPS.</summary>
public enum DriverState
{
    /// <summary>The UPS is configured but disabled.</summary>
    Disabled,

    /// <summary>The driver is being created and has not reported anything yet.</summary>
    Starting,

    /// <summary>The driver runs and is trying to reach the device.</summary>
    Connecting,

    /// <summary>The driver talks to the device and publishes data.</summary>
    Connected,

    /// <summary>The driver runs but lost the device; it keeps retrying.</summary>
    Disconnected,

    /// <summary>The driver stopped with an error (bad configuration, missing device support...).</summary>
    Failed,
}

/// <summary>Whether the data of a UPS can be served, in the terms of the NUT protocol errors.</summary>
public enum DataAvailability
{
    /// <summary>Fresh data.</summary>
    Available,

    /// <summary>The driver runs but has no fresh data (ERR DATA-STALE).</summary>
    Stale,

    /// <summary>No driver is running for this UPS (ERR DRIVER-NOT-CONNECTED).</summary>
    DriverNotConnected,
}
