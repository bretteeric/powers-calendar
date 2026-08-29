namespace OfficeCal.Services;

public interface IIcsService
{
    /// <summary>單筆事件的 .ics 內容（含其所有未取消的 occurrence）。權限同事件明細。</summary>
    Task<string> ExportEventAsync(int eventId, CancellationToken ct = default);

    /// <summary>個人訂閱 feed。匿名端點，以 token 授權；token 無效時丟 NotFoundException。</summary>
    Task<string> BuildFeedAsync(string token, CancellationToken ct = default);
}
