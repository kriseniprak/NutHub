using Microsoft.Extensions.Time.Testing;
using NutHub.Core.Model;
using NutHub.Protocol.Tracking;

namespace NutHub.Protocol.Tests.Units;

public sealed class TrackingStoreTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void New_entries_are_pending_then_hold_their_result()
    {
        var store = new TrackingStore(_time, new NutProtocolOptions());
        string id = store.Create();

        Assert.True(Guid.TryParse(id, out Guid guid));
        Assert.Equal(4, guid.Version);
        Assert.Equal(id.ToLowerInvariant(), id);
        Assert.Equal("PENDING", store.Get(id));

        store.Complete(id, "SUCCESS");
        Assert.Equal("SUCCESS", store.Get(id.ToUpperInvariant())); // ids ignore case, like upsd
    }

    [Fact]
    public void Unknown_ids_answer_err_unknown()
    {
        var store = new TrackingStore(_time, new NutProtocolOptions());
        Assert.Equal("ERR UNKNOWN", store.Get("1bd31808-cb49-4aec-9d75-d056e6f018d2"));
    }

    [Fact]
    public void Results_expire_after_the_retention()
    {
        var store = new TrackingStore(_time, new NutProtocolOptions { TrackingRetention = TimeSpan.FromHours(1) });
        string id = store.Create();
        store.Complete(id, "SUCCESS");

        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal("SUCCESS", store.Get(id));

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("ERR UNKNOWN", store.Get(id));
        store.Cleanup();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Capacity_drops_the_oldest()
    {
        var store = new TrackingStore(_time, new NutProtocolOptions { TrackingCapacity = 3 });
        string first = store.Create();
        string[] later = [store.Create(), store.Create(), store.Create()];

        Assert.Equal(3, store.Count);
        Assert.Equal("ERR UNKNOWN", store.Get(first));
        Assert.All(later, id => Assert.Equal("PENDING", store.Get(id)));
    }

    [Theory]
    [InlineData(CommandStatus.Success, "SUCCESS")]
    [InlineData(CommandStatus.NotSupported, "ERR UNKNOWN")]
    [InlineData(CommandStatus.InvalidValue, "ERR INVALID-ARGUMENT")]
    [InlineData(CommandStatus.InvalidArgument, "ERR INVALID-ARGUMENT")]
    [InlineData(CommandStatus.TooLong, "ERR INVALID-ARGUMENT")]
    [InlineData(CommandStatus.Failed, "ERR FAILED")]
    [InlineData(CommandStatus.DriverNotConnected, "ERR FAILED")]
    public void Results_use_the_upsd_words(CommandStatus status, string expected)
    {
        Assert.Equal(expected, TrackingStore.ResultOf(new CommandResult(status)));
    }
}
