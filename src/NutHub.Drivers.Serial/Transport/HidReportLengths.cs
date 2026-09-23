namespace NutHub.Drivers.Serial.Transport;

/// <summary>
/// The report sizes a HID report descriptor declares, all the USB bridge transport needs from it: the longest input and
/// output reports, report id byte included as HidSharp counts them (0 when there are none), and whether the input
/// reports are numbered, which is what decides whether hidraw's input data starts with the report id (the kernel's
/// report_enum.numbered, which is per report type). Only the items that size reports are read: Report Size, Report
/// Count, Report ID, Push, Pop and the Input and Output main items.
/// </summary>
internal readonly record struct HidReportLengths(int MaxInput, int MaxOutput, bool InputReportsNumbered)
{
    private const byte InputItem = 0x80;
    private const byte OutputItem = 0x90;
    private const byte ReportSizeItem = 0x74;
    private const byte ReportIdItem = 0x84;
    private const byte ReportCountItem = 0x94;
    private const byte PushItem = 0xA4;
    private const byte PopItem = 0xB4;
    private const byte LongItem = 0xFE;

    public static HidReportLengths Measure(ReadOnlySpan<byte> descriptor)
    {
        var bits = new Dictionary<(byte Kind, uint Id), long>();
        var stack = new Stack<(uint Size, uint Count, uint Id)>();
        uint size = 0, count = 0, id = 0;
        int position = 0;
        while (position < descriptor.Length)
        {
            byte prefix = descriptor[position];
            if (prefix == LongItem)
            {
                // Long item: prefix, data size, tag, data.
                position += position + 1 < descriptor.Length ? 3 + descriptor[position + 1] : descriptor.Length;
                continue;
            }

            int length = (prefix & 0x03) == 3 ? 4 : prefix & 0x03;
            if (position + 1 + length > descriptor.Length)
            {
                break;
            }

            uint data = 0;
            for (int i = 0; i < length; i++)
            {
                data |= (uint)descriptor[position + 1 + i] << (8 * i);
            }

            switch ((byte)(prefix & 0xFC))
            {
                case InputItem or OutputItem:
                    byte kind = (byte)(prefix & 0xFC);
                    bits[(kind, id)] = bits.GetValueOrDefault((kind, id)) + ((long)size * count);
                    break;
                case ReportSizeItem:
                    size = data;
                    break;
                case ReportCountItem:
                    count = data;
                    break;
                case ReportIdItem:
                    id = data;
                    break;
                case PushItem:
                    stack.Push((size, count, id));
                    break;
                case PopItem when stack.Count > 0:
                    (size, count, id) = stack.Pop();
                    break;
            }

            position += 1 + length;
        }

        return new HidReportLengths(Longest(bits, InputItem), Longest(bits, OutputItem),
                                    bits.Keys.Any(k => k.Kind == InputItem && k.Id != 0));
    }

    private static int Longest(Dictionary<(byte Kind, uint Id), long> bits, byte kind)
    {
        long longest = bits.Where(b => b.Key.Kind == kind).Select(b => b.Value).DefaultIfEmpty(-1).Max();
        return longest < 0 ? 0 : (int)Math.Min(1 + ((longest + 7) / 8), 4096);
    }
}
