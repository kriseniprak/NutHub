using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NutHub.Protocol.Security;

namespace NutHub.Protocol.Tests.Units;

public sealed class LoginThrottleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly LoginThrottle _throttle;

    public LoginThrottleTests()
    {
        _throttle = new LoginThrottle(_time, new NutProtocolOptions(), NullLogger.Instance);
    }

    [Fact]
    public void Ten_failures_within_five_minutes_lock_the_address_out_for_five_minutes()
    {
        var address = IPAddress.Parse("192.168.1.20");
        for (int i = 0; i < 9; i++)
        {
            _throttle.RecordFailure(address);
            _time.Advance(TimeSpan.FromSeconds(20));
        }

        Assert.False(_throttle.IsLockedOut(address));
        _throttle.RecordFailure(address);
        Assert.True(_throttle.IsLockedOut(address));
        Assert.False(_throttle.IsLockedOut(IPAddress.Parse("192.168.1.21")));

        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(_throttle.IsLockedOut(address));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(_throttle.IsLockedOut(address));
    }

    [Fact]
    public void Failures_spread_over_more_than_the_window_do_not_lock_out()
    {
        var address = IPAddress.Parse("10.0.0.5");
        for (int i = 0; i < 30; i++)
        {
            _throttle.RecordFailure(address);
            _time.Advance(TimeSpan.FromSeconds(40));
        }

        Assert.False(_throttle.IsLockedOut(address));
    }

    [Fact]
    public void Ipv4_mapped_and_plain_ipv4_are_the_same_client()
    {
        for (int i = 0; i < 10; i++)
        {
            _throttle.RecordFailure(IPAddress.Parse("::ffff:10.1.2.3"));
        }

        Assert.True(_throttle.IsLockedOut(IPAddress.Parse("10.1.2.3")));
    }

    [Fact]
    public void Ipv6_clients_are_grouped_by_64_bit_prefix()
    {
        for (int i = 0; i < 10; i++)
        {
            _throttle.RecordFailure(IPAddress.Parse($"2001:db8:1:2::{i + 1:x}"));
        }

        Assert.True(_throttle.IsLockedOut(IPAddress.Parse("2001:db8:1:2:ffff::1")));
        Assert.False(_throttle.IsLockedOut(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public void Cleanup_forgets_old_addresses()
    {
        _throttle.RecordFailure(IPAddress.Parse("10.0.0.1"));
        Assert.Equal(1, _throttle.TrackedAddresses);
        _time.Advance(TimeSpan.FromMinutes(6));
        _throttle.Cleanup();
        Assert.Equal(0, _throttle.TrackedAddresses);
    }
}
