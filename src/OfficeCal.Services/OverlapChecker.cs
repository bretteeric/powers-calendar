namespace OfficeCal.Services;

/// <summary>
/// 規格 5.1 的重疊判定：新起 &lt; 舊迄 AND 新迄 &gt; 舊起。
/// 頭尾相接（09:00–10:00 與 10:00–11:00）不算衝突。
/// </summary>
public static class OverlapChecker
{
    public static bool Overlaps(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
        => aStart < bEnd && aEnd > bStart;
}
