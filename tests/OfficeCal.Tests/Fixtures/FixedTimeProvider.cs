namespace OfficeCal.Tests.Fixtures;

/// <summary>可控制的時鐘。傳入的是 Asia/Taipei 當地時間。</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTime taipeiLocalNow)
        => _utcNow = new DateTimeOffset(taipeiLocalNow, TimeSpan.FromHours(8)).ToUniversalTime();

    public void SetTaipeiNow(DateTime taipeiLocalNow)
        => _utcNow = new DateTimeOffset(taipeiLocalNow, TimeSpan.FromHours(8)).ToUniversalTime();

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
