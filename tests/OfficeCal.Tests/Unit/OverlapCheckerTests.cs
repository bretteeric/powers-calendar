using OfficeCal.Services;
using Xunit;

namespace OfficeCal.Tests.Unit;

public class OverlapCheckerTests
{
    private static DateTime T(int hour, int minute = 0) => new(2026, 9, 7, hour, minute, 0);

    [Theory]
    // 完全重疊
    [InlineData(9, 10, 9, 10, true)]
    // 部分重疊（新的較早開始）
    [InlineData(9, 11, 10, 12, true)]
    // 部分重疊（新的較晚開始）
    [InlineData(10, 12, 9, 11, true)]
    // 包含
    [InlineData(9, 12, 10, 11, true)]
    // 被包含
    [InlineData(10, 11, 9, 12, true)]
    // 頭尾相接 —— 規格 5.1 明訂不算衝突
    [InlineData(9, 10, 10, 11, false)]
    [InlineData(10, 11, 9, 10, false)]
    // 完全分離
    [InlineData(9, 10, 14, 15, false)]
    public void 重疊判定(int aStart, int aEnd, int bStart, int bEnd, bool expected)
        => Assert.Equal(expected, OverlapChecker.Overlaps(T(aStart), T(aEnd), T(bStart), T(bEnd)));
}
