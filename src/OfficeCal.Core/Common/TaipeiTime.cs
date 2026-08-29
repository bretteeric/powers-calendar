namespace OfficeCal.Core.Common;

/// <summary>
/// 全系統時間為 Asia/Taipei 當地時間。台灣無日光節約，固定 +08:00 位移完全正確，
/// 且避開作業系統時區資料庫的 ID 差異。
/// </summary>
public static class TaipeiTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    public static DateTime Now(TimeProvider clock)
        => DateTime.SpecifyKind(clock.GetUtcNow().ToOffset(Offset).DateTime, DateTimeKind.Unspecified);
}
