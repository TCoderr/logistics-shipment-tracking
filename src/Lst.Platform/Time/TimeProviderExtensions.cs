namespace Lst.Platform.Time;

public static class TimeProviderExtensions
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>
    /// PostgreSQL timestamptz mikro saniye çözünürlüğündedir; uygulama değerlerini aynı çözünürlüğe indirerek
    /// ilk yanıt ile DB'den okunan değerin birebir aynı olmasını sağlar.
    /// </summary>
    public static DateTimeOffset GetUtcNowMicroseconds(this TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var now = timeProvider.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % TicksPerMicrosecond), TimeSpan.Zero);
    }
}
