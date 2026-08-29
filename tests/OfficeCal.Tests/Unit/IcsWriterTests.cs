using System.Text;
using OfficeCal.Services;
using Xunit;

namespace OfficeCal.Tests.Unit;

public class IcsWriterTests
{
    [Fact]
    public void 跳脫反斜線分號逗號與換行()
    {
        Assert.Equal(@"a\\b", IcsWriter.Escape(@"a\b"));
        Assert.Equal(@"a\;b", IcsWriter.Escape("a;b"));
        Assert.Equal(@"a\,b", IcsWriter.Escape("a,b"));
        Assert.Equal(@"a\nb", IcsWriter.Escape("a\nb"));
        Assert.Equal(@"a\nb", IcsWriter.Escape("a\r\nb"));
    }

    [Fact]
    public void 短行不折行()
    {
        var sb = new StringBuilder();
        IcsWriter.AppendFolded(sb, "SUMMARY:短標題");
        Assert.Equal("SUMMARY:短標題\r\n", sb.ToString());
    }

    [Fact]
    public void 長行以七十五個位元組為界折行且續行以空白開頭()
    {
        var sb = new StringBuilder();
        IcsWriter.AppendFolded(sb, "SUMMARY:" + new string('A', 200));

        var lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length > 1);
        Assert.All(lines, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75,
                                            $"這一行有 {Encoding.UTF8.GetByteCount(l)} 個位元組"));
        Assert.All(lines.Skip(1), l => Assert.StartsWith(" ", l));

        var rebuilt = string.Concat(lines.Select((l, i) => i == 0 ? l : l[1..]));
        Assert.Equal("SUMMARY:" + new string('A', 200), rebuilt);
    }

    [Fact]
    public void 中文標題折行不會切斷UTF8位元組序列()
    {
        var title = string.Concat(Enumerable.Repeat("會議室預約通知", 20));   // 每字 3 bytes
        var sb = new StringBuilder();
        IcsWriter.AppendFolded(sb, "SUMMARY:" + title);

        var lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
        Assert.DoesNotContain("�", sb.ToString());   // 沒有替換字元＝沒有切壞

        var rebuilt = string.Concat(lines.Select((l, i) => i == 0 ? l : l[1..]));
        Assert.Equal("SUMMARY:" + title, rebuilt);
    }

    [Fact]
    public void 台北時區區塊固定為正八小時且無日光節約()
    {
        var vtz = IcsWriter.TaipeiVTimeZone();
        Assert.Contains("TZID:Asia/Taipei", vtz);
        Assert.Contains("TZOFFSETFROM:+0800", vtz);
        Assert.Contains("TZOFFSETTO:+0800", vtz);
        Assert.DoesNotContain("DAYLIGHT", vtz);
    }
}
