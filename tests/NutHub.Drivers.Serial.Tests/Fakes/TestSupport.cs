using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Drivers.Serial.Megatec;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.Tests.Fakes;

/// <summary>A Q* link answering from a table (command with CR to reply with CR); silence for anything else.</summary>
internal sealed class TableQxLink : IQxLink
{
    public Dictionary<string, string> Replies { get; } = new(StringComparer.Ordinal);

    public List<string> Sent { get; } = [];

    public TableQxLink Reply(string command, string reply)
    {
        Replies[command + "\r"] = reply;
        return this;
    }

    public Task<QxAnswer> ExchangeAsync(string command, CancellationToken cancellationToken)
    {
        Sent.Add(command);
        return Task.FromResult(Replies.TryGetValue(command, out string? reply) ? new QxAnswer(reply) : QxAnswer.None);
    }
}

/// <summary>Runs a driver's poll loop in the background until disposed.</summary>
internal sealed class DriverRun : IAsyncDisposable
{
    private readonly IUpsDriver _driver;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    private DriverRun(IUpsDriver driver, IDriverContext context)
    {
        _driver = driver;
        _run = Task.Run(() => driver.RunAsync(context, _stop.Token));
    }

    public static DriverRun Start(IUpsDriver driver, IDriverContext context) => new(driver, context);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            // The normal end of a run.
        }

        await _driver.DisposeAsync();
        _stop.Dispose();
    }
}

internal static class TestSettings
{
    public static MegatecSettings Megatec(string protocol = "megatec") =>
        new() { Transport = new TcpSettings("127.0.0.1", 9), Protocol = protocol };

    public static QxEngine Engine(string protocol, IQxLink link, MegatecSettings? settings = null, TimeProvider? time = null) =>
        new(QxProtocols.Create(protocol, new QxProtocolOptions()), link, settings ?? Megatec(protocol),
            time ?? TimeProvider.System, NullLogger.Instance);

    public static Dictionary<string, string> Options(params (string Key, string Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
}
