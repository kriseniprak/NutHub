using NutHub.Core.Configuration;

namespace NutHub.Storage.History;

/// <summary>
/// How history is laid out in time: raw samples for the last few days, 5-minute roll-ups for the whole retention,
/// and the bucket widths queries may use. Every timestamp here is in Unix seconds and every bucket is aligned to a
/// multiple of its width since the epoch, so roll-ups always fit exactly into query buckets.
/// </summary>
internal static class HistoryLayout
{
    /// <summary>The width of a roll-up bucket.</summary>
    public const int RollupSeconds = 300;

    /// <summary>Raw samples are kept this long at most (less when the whole retention is shorter).</summary>
    public const int MaxRawRetentionDays = 7;

    public const int DefaultMaxPoints = 500;

    /// <summary>A ceiling on points per series, so one request cannot make the server build millions of buckets.</summary>
    public const int MaxPointsLimit = 10_000;

    public const int SecondsPerDay = 86_400;

    /// <summary>Key in storage_state: every sample before this time has been rolled up.</summary>
    public const string WatermarkKey = "rollup_watermark";

    // Widths a person reads easily on a chart axis. From 300 up, every width is a multiple of the roll-up width.
    private static readonly int[] NiceSteps =
        [1, 2, 5, 10, 15, 20, 30, 60, 120, 180, 300, 600, 900, 1200, 1800, 3600, 7200, 10800, 21600, 43200, SecondsPerDay];

    public static int RawRetentionDays(HistorySettings settings) =>
        Math.Clamp(settings.RetentionDays, 1, MaxRawRetentionDays);

    public static int RollupRetentionDays(HistorySettings settings) => Math.Max(1, settings.RetentionDays);

    /// <summary>
    /// The smallest readable bucket width that is at least <paramref name="minimumStep"/> (the sampling interval: a
    /// narrower bucket would hold no sample), a multiple of <paramref name="multipleOf"/> (the roll-up width when
    /// roll-ups are read) and gives at most <paramref name="maxPoints"/> aligned buckets over [from, to].
    /// </summary>
    public static int ChooseStep(long fromSeconds, long toSeconds, int maxPoints, int minimumStep, int multipleOf)
    {
        maxPoints = Math.Max(1, maxPoints);
        minimumStep = Math.Max(1, minimumStep);
        multipleOf = Math.Max(1, multipleOf);
        toSeconds = Math.Max(toSeconds, fromSeconds);

        foreach (int step in NiceSteps)
        {
            if (step >= minimumStep && step % multipleOf == 0 &&
                BucketCount(fromSeconds, toSeconds, step) <= maxPoints)
            {
                return step;
            }
        }

        // Longer than a day per bucket: whole days, the fewest that fit.
        long span = toSeconds - fromSeconds + 1;
        long days = Math.Max(2, span / ((long)maxPoints * SecondsPerDay));
        while (BucketCount(fromSeconds, toSeconds, days * SecondsPerDay) > maxPoints)
        {
            days++;
        }

        return (int)Math.Min(days * SecondsPerDay, int.MaxValue / 2);
    }

    /// <summary>The number of aligned buckets of width <paramref name="step"/> touching [from, to].</summary>
    public static long BucketCount(long fromSeconds, long toSeconds, long step) =>
        FloorDiv(toSeconds, step) - FloorDiv(fromSeconds, step) + 1;

    /// <summary>The start of the aligned bucket containing <paramref name="seconds"/>.</summary>
    public static long AlignDown(long seconds, long step) => FloorDiv(seconds, step) * step;

    private static long FloorDiv(long value, long divisor)
    {
        long quotient = Math.DivRem(value, divisor, out long remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
