using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.Tests.Support;
using NutHub.Drivers.Hid.Transport;
using NutHub.Drivers.Hid.UsbHid.Subdrivers;
using static NutHub.Drivers.Hid.Tests.Support.DescriptorBuilder;

namespace NutHub.Drivers.Hid.Tests.Descriptors;

public sealed class ReportDescriptorParserTests
{
    public static TheoryData<string> Dumps => new(RealDeviceDump.All);

    [Theory]
    [MemberData(nameof(Dumps))]
    public void Real_descriptors_parse_into_the_fields_NUT_found(string name)
    {
        RealDeviceDump dump = RealDeviceDump.Load(name);
        HidReportDescriptor descriptor = HidReportDescriptorParser.Parse(dump.Descriptor);
        HidUsageTable usages = SubdriverFor(dump).Usages;

        Assert.Empty(descriptor.Warnings);
        Assert.True(descriptor.IsPowerDevice);
        Assert.NotEmpty(dump.Values);
        foreach (RealDeviceDump.NutValue value in dump.Values)
        {
            Assert.True(HidPath.TryParse(value.Path, usages, out HidPath path), $"{dump.Source}: unknown usage in {value.Path}");
            HidField? field = FindField(descriptor, value, path);
            Assert.True(field is not null,
                        $"{dump.Source}: no {value.Kind} field {value.Path} in report 0x{value.ReportId:x2} at bit {value.Offset}, size {value.Size}.");
            Assert.Equal(value.Path, field.Path.ToString(usages));
        }
    }

    [Theory]
    [MemberData(nameof(Dumps))]
    public void Real_reports_decode_to_the_values_NUT_read(string name)
    {
        RealDeviceDump dump = RealDeviceDump.Load(name);
        HidReportDescriptor raw = HidReportDescriptorParser.Parse(dump.Descriptor);
        HidReportDescriptor repaired = HidReportDescriptorParser.Parse(dump.Descriptor);
        UsbHidSubdriver subdriver = SubdriverFor(dump);
        subdriver.FixReportDescriptor(Info(dump), repaired);

        int compared = 0;
        foreach (RealDeviceDump.NutValue value in dump.Values)
        {
            Assert.True(HidPath.TryParse(value.Path, subdriver.Usages, out HidPath path));
            HidField field = FindField(raw, value, path)!;

            // NUT 2.7 decoded values beyond the declared logical range, maximums that overflowed into the sign bit
            // and 32-bit signed fields differently from current NUT, which this port follows (NUT issue 1023 and
            // the GetValue rewrite of 2.8.0).
            if (dump.NutVersion < new Version(2, 8) && DecodedDifferentlyBefore28(field, value.Report))
            {
                continue;
            }

            // NUT prints with %g (6 significant digits). Depending on its version it dumped the values before or
            // after the subdriver repaired the descriptor, so either reading may be the reference.
            double before = HidValueCodec.GetValue(value.Report, field);
            double after = HidValueCodec.GetValue(value.Report, FindField(repaired, value, path)!);
            Assert.True(Close(before, value.Value) || Close(after, value.Value),
                        $"{dump.Source}: {value.Path} decoded as {before} / {after}, NUT read {value.Value}.");
            compared++;
        }

        Assert.True(compared >= dump.Values.Count * 9 / 10, $"Only {compared} of {dump.Values.Count} values were compared.");
    }

    [Fact]
    public void Collections_add_their_usage_and_an_index_node_for_vendor_types()
    {
        byte[] bytes = new DescriptorBuilder()
            .UsagePage(0x84).Usage("UPS").Collection(Application)
            .Usage("Flow").Collection(0x84)
            .ReportId(0x12).Value("ConfigVoltage", 8, 0, 255)
            .EndCollection()
            .EndCollection()
            .ToArray();

        HidField field = Assert.Single(HidReportDescriptorParser.Parse(bytes).Fields);

        Assert.Equal("UPS.Flow.[4].ConfigVoltage", field.Path.ToString());
        Assert.Equal(0x12, field.ReportId);
        Assert.Equal(HidReportKind.Feature, field.Kind);
    }

    [Fact]
    public void Fields_without_a_usage_advance_the_offset_but_are_not_stored()
    {
        byte[] bytes = new DescriptorBuilder()
            .UsagePage(0x84).Usage("UPS").Collection(Application)
            .ReportId(1)
            .Usage("ACPresent").Usage("Discharging")
            .LogicalMinimum(0).LogicalMaximum(1).ReportSize(1).ReportCount(3).Feature() // third field has no usage
            .ReportCount(5).Feature(Constant)
            .Value("RemainingCapacity", 8, 0, 100)
            .EndCollection()
            .ToArray();

        HidReportDescriptor descriptor = HidReportDescriptorParser.Parse(bytes);

        Assert.Equal(["UPS.ACPresent", "UPS.Discharging", "UPS.RemainingCapacity"], descriptor.Fields.Select(f => f.Path.ToString()));
        Assert.Equal([0, 1, 8], descriptor.Fields.Select(f => f.BitOffset));
        Assert.Equal(2, descriptor.GetReportLength(HidReportKind.Feature, 1));
    }

    [Fact]
    public void Each_report_kind_and_id_has_its_own_offsets()
    {
        byte[] bytes = new DescriptorBuilder()
            .UsagePage(0x84).Usage("UPS").Collection(Application)
            .ReportId(3).Value("RemainingCapacity", 8, 0, 100).Value("RemainingCapacity", 8, 0, 100, input: true)
            .ReportId(4).Value("RunTimeToEmpty", 16, 0, 65535)
            .ReportId(3).Value("RunTimeToEmpty", 16, 0, 65535)
            .EndCollection()
            .ToArray();

        HidReportDescriptor descriptor = HidReportDescriptorParser.Parse(bytes);

        Assert.Equal([(3, 0, HidReportKind.Feature), (3, 0, HidReportKind.Input), (4, 0, HidReportKind.Feature), (3, 8, HidReportKind.Feature)],
                     descriptor.Fields.Select(f => ((int)f.ReportId, f.BitOffset, f.Kind)));
        Assert.Equal(3, descriptor.GetReportLength(HidReportKind.Feature, 3));
        Assert.Equal(1, descriptor.GetReportLength(HidReportKind.Input, 3));
        Assert.Equal([3, 4], descriptor.GetReportIds(HidReportKind.Feature).Select(id => (int)id));
        Assert.True(descriptor.UsesReportIds);
    }

    [Fact]
    public void A_logical_maximum_written_as_a_negative_number_is_read_as_unsigned()
    {
        // 0..65535 encoded in two bytes reads as 0..-1: NUT takes the maximum as unsigned.
        byte[] bytes = new DescriptorBuilder()
            .UsagePage(0x84).Usage("UPS").Collection(Application)
            .Usage("Voltage").LogicalMinimum(0).Raw(0x26, 0xFF, 0xFF).ReportSize(16).ReportCount(1).Feature()
            .EndCollection()
            .ToArray();

        HidField field = Assert.Single(HidReportDescriptorParser.Parse(bytes).Fields);

        Assert.Equal(65535, field.LogicalMaximum);
        Assert.True(field.LogicalMaximumAssumed);
    }

    [Fact]
    public void Push_and_pop_restore_the_global_state()
    {
        byte[] bytes = new DescriptorBuilder()
            .UsagePage(0x84).Usage("UPS").Collection(Application)
            .ReportId(1).LogicalMinimum(0).LogicalMaximum(100).ReportSize(8).ReportCount(1)
            .Push()
            .ReportId(2).LogicalMinimum(-1).LogicalMaximum(32767).ReportSize(16)
            .Usage("DelayBeforeShutdown").Feature()
            .Pop()
            .Usage("RemainingCapacity").Feature()
            .EndCollection()
            .ToArray();

        HidReportDescriptor descriptor = HidReportDescriptorParser.Parse(bytes);

        HidField delay = descriptor.Fields[0];
        HidField capacity = descriptor.Fields[1];
        Assert.Equal((2, 16, -1L, 32767L), (delay.ReportId, delay.BitSize, delay.LogicalMinimum, delay.LogicalMaximum));
        Assert.Equal((1, 8, 0L, 100L), (capacity.ReportId, capacity.BitSize, capacity.LogicalMinimum, capacity.LogicalMaximum));
    }

    [Fact]
    public void Usage_ranges_and_extended_usages_name_the_fields()
    {
        byte[] bytes = new DescriptorBuilder()
            .UsagePage(0x84).Usage("UPS").Collection(Application)
            .UsageMinimum(0x30).UsageMaximum(0x32) // Voltage, Current, Frequency
            .LogicalMinimum(0).LogicalMaximum(255).ReportSize(8).ReportCount(3).Feature()
            .ExtendedUsage(0x00850066) // Battery System page: RemainingCapacity
            .ReportCount(1).Feature()
            .EndCollection()
            .ToArray();

        HidReportDescriptor descriptor = HidReportDescriptorParser.Parse(bytes);

        Assert.Equal(["UPS.Voltage", "UPS.Current", "UPS.Frequency", "UPS.RemainingCapacity"],
                     descriptor.Fields.Select(f => f.Path.ToString()));
    }

    [Fact]
    public void Unit_and_exponent_are_kept_with_the_field()
    {
        HidReportDescriptor descriptor = HidReportDescriptorParser.Parse(SyntheticUps.Descriptor);

        HidField voltage = descriptor.Fields.Single(f => f.Path.ToString() == "UPS.Input.Voltage");
        HidField frequency = descriptor.Fields.Single(f => f.Path.ToString() == "UPS.Input.Frequency");

        Assert.Equal((0x00F0D121u, (sbyte)7), (voltage.Unit, voltage.UnitExponent));
        Assert.Equal((0u, (sbyte)-1), (frequency.Unit, frequency.UnitExponent));
        Assert.Equal(16, frequency.BitOffset);
    }

    [Fact]
    public void A_truncated_descriptor_keeps_the_fields_before_the_damage()
    {
        byte[] bytes = new DescriptorBuilder()
            .UsagePage(0x84).Usage("UPS").Collection(Application)
            .ReportId(1).Value("RemainingCapacity", 8, 0, 100)
            .Raw(0x27, 0xFF) // a 4-byte item with only one byte left
            .ToArray();

        HidReportDescriptor descriptor = HidReportDescriptorParser.Parse(bytes);

        Assert.Single(descriptor.Fields);
        Assert.Contains(descriptor.Warnings, w => w.Contains("Truncated", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unbalanced_end_collection_stops_the_parse_with_a_warning()
    {
        byte[] bytes = new DescriptorBuilder()
            .UsagePage(0x84).Usage("UPS").Collection(Application)
            .Value("RemainingCapacity", 8, 0, 100)
            .EndCollection().EndCollection()
            .Value("RunTimeToEmpty", 16, 0, 65535)
            .ToArray();

        HidReportDescriptor descriptor = HidReportDescriptorParser.Parse(bytes);

        Assert.Single(descriptor.Fields);
        Assert.NotEmpty(descriptor.Warnings);
    }

    [Fact]
    public void An_oversized_descriptor_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => HidReportDescriptorParser.Parse(new byte[HidReportDescriptorParser.MaxDescriptorLength + 1]));
    }

    [Fact]
    public void A_keyboard_is_not_a_power_device()
    {
        byte[] keyboard = new DescriptorBuilder()
            .UsagePage(0x01).Usage(0x06).Collection(Application)
            .UsagePage(0x07).UsageMinimum(0xE0).UsageMaximum(0xE7)
            .LogicalMinimum(0).LogicalMaximum(1).ReportSize(1).ReportCount(8).Input()
            .EndCollection()
            .ToArray();

        HidReportDescriptor descriptor = HidReportDescriptorParser.Parse(keyboard);

        Assert.False(descriptor.IsPowerDevice);
        Assert.Equal([0x00010006u], descriptor.ApplicationUsages);
        Assert.True(HidReportDescriptorParser.Parse(SyntheticUps.Descriptor).IsPowerDevice);
    }

    [Fact]
    public void Random_bytes_never_crash_the_parser()
    {
        var random = new Random(1234);
        for (int i = 0; i < 2000; i++)
        {
            byte[] bytes = new byte[random.Next(0, 400)];
            random.NextBytes(bytes);
            HidReportDescriptor descriptor = HidReportDescriptorParser.Parse(bytes);
            Assert.All(descriptor.Fields, f => Assert.InRange(f.BitSize, 0, 1024));
        }
    }

    internal static UsbHidSubdriver SubdriverFor(RealDeviceDump dump) =>
        SubdriverCatalog.Select(Info(dump), isPowerDevice: true, "auto", productIdGiven: false)!;

    private static HidDeviceInfo Info(RealDeviceDump dump) =>
        new("/dev/hidraw0", dump.VendorId, dump.ProductId, null, null, null);

    private static HidField? FindField(HidReportDescriptor descriptor, RealDeviceDump.NutValue value, HidPath path) =>
        descriptor.Fields.FirstOrDefault(f => f.Kind == value.Kind && f.ReportId == value.ReportId &&
                                              f.BitOffset == value.Offset && f.BitSize == value.Size && f.Path == path);

    private static bool DecodedDifferentlyBefore28(HidField field, byte[] report)
    {
        if (field.LogicalMinimum < 0)
        {
            return field.BitSize >= 32;
        }

        var bits = new HidField
        {
            Path = field.Path,
            ReportId = field.ReportId,
            Kind = field.Kind,
            BitOffset = field.BitOffset,
            BitSize = field.BitSize,
            LogicalMinimum = 0,
            LogicalMaximum = field.BitSize >= 63 ? long.MaxValue : (1L << field.BitSize) - 1,
        };
        return field.LogicalMaximumAssumed || HidValueCodec.GetLogical(report, bits) > field.LogicalMaximum;
    }

    private static bool Close(double actual, double expected) =>
        Math.Abs(actual - expected) <= 5e-6 * Math.Max(1, Math.Abs(expected));
}
