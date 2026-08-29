using System.Text;

namespace OfficeCal.Services;

/// <summary>
/// RFC 5545 的文字層細節：跳脫、折行、時區區塊。
/// 折行以 75 個「octet」為界並避開 UTF-8 續接位元組——標題是中文，
/// 以字元數折行會切斷位元組序列而產生亂碼。
/// </summary>
public static class IcsWriter
{
    private const string Crlf = "\r\n";
    private const int MaxOctets = 75;

    public static string Escape(string value)
        => value.Replace("\\", "\\\\")
                .Replace("\r\n", "\\n")
                .Replace("\n", "\\n")
                .Replace("\r", "\\n")
                .Replace(";", "\\;")
                .Replace(",", "\\,");

    public static void AppendFolded(StringBuilder sb, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= MaxOctets)
        {
            sb.Append(line).Append(Crlf);
            return;
        }

        var pos = 0;
        var first = true;
        while (pos < bytes.Length)
        {
            // 續行要先加一個空白，所以可用的位元組少一個
            var budget = first ? MaxOctets : MaxOctets - 1;
            var take = Math.Min(budget, bytes.Length - pos);

            // 不要停在 UTF-8 的續接位元組（10xxxxxx）上
            while (take > 0 && pos + take < bytes.Length && (bytes[pos + take] & 0xC0) == 0x80)
                take--;

            if (take == 0) take = Math.Min(budget, bytes.Length - pos);   // 理論上不會發生的保險

            if (!first) sb.Append(' ');
            sb.Append(Encoding.UTF8.GetString(bytes, pos, take)).Append(Crlf);

            pos += take;
            first = false;
        }
    }

    /// <summary>本地時間格式：20260914T100000。</summary>
    public static string Local(DateTime dt) => dt.ToString("yyyyMMdd'T'HHmmss");

    /// <summary>UTC 格式，供 DTSTAMP 使用：20260829T031500Z。</summary>
    public static string Utc(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'");

    /// <summary>
    /// 台北時區區塊。只寫 TZID 而不附 VTIMEZONE，Outlook 訂閱時會顯示錯誤時間。
    /// 台灣自 1980 年起無日光節約，因此只需要一個固定 +08:00 的 STANDARD 區塊。
    /// </summary>
    public static string TaipeiVTimeZone()
    {
        var sb = new StringBuilder();
        AppendFolded(sb, "BEGIN:VTIMEZONE");
        AppendFolded(sb, "TZID:Asia/Taipei");
        AppendFolded(sb, "X-LIC-LOCATION:Asia/Taipei");
        AppendFolded(sb, "BEGIN:STANDARD");
        AppendFolded(sb, "DTSTART:19800101T000000");
        AppendFolded(sb, "TZOFFSETFROM:+0800");
        AppendFolded(sb, "TZOFFSETTO:+0800");
        AppendFolded(sb, "TZNAME:CST");
        AppendFolded(sb, "END:STANDARD");
        AppendFolded(sb, "END:VTIMEZONE");
        return sb.ToString();
    }
}
