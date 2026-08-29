using System.Text;
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Common;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;

namespace OfficeCal.Services;

public class IcsService : IIcsService
{
    /// <summary>feed 的時間窗口：過去 90 天至未來 730 天。</summary>
    private const int FeedPastDays = 90;
    private const int FeedFutureDays = 730;

    private const string UidDomain = "calendar.local";

    private readonly OfficeCalDbContext _db;
    private readonly IUserRepository _users;
    private readonly IEventOccurrenceRepository _occurrences;
    private readonly IUserContext _me;
    private readonly TimeProvider _clock;

    public IcsService(OfficeCalDbContext db, IUserRepository users,
                      IEventOccurrenceRepository occurrences, IUserContext me, TimeProvider clock)
        => (_db, _users, _occurrences, _me, _clock) = (db, users, occurrences, me, clock);

    public async Task<string> ExportEventAsync(int eventId, CancellationToken ct = default)
    {
        var ev = await _db.Events.AsNoTracking()
            .Include(e => e.Room)
            .Include(e => e.Attendees)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct)
            ?? throw new NotFoundException("找不到事件");

        var isAttendee = ev.Attendees.Any(a => a.UserId == _me.UserId);
        if (ev.OwnerId != _me.UserId && !isAttendee && ev.RoomId is null)
            throw new ForbiddenException("沒有權限匯出此事件");

        var rows = await _db.EventOccurrences.AsNoTracking()
            .Include(o => o.Event!).Include(o => o.Room)
            .Where(o => o.EventId == eventId && !o.IsCancelled)
            .OrderBy(o => o.StartAt)
            .ToListAsync(ct);

        return Build(ev.Title, rows);
    }

    public async Task<string> BuildFeedAsync(string token, CancellationToken ct = default)
    {
        var user = await _users.GetByFeedTokenAsync(token, ct)
                   ?? throw new NotFoundException("訂閱網址無效");

        var now = TaipeiTime.Now(_clock);
        var rows = await _occurrences.GetRangeForUserAsync(
            user.Id, now.AddDays(-FeedPastDays), now.AddDays(FeedFutureDays), ct);

        return Build($"{user.DisplayName} 的行事曆", rows);
    }

    /// <summary>
    /// 輸出已展開的逐筆 VEVENT，不輸出 RRULE——相容性最好，
    /// 且與資料庫的權威占用表完全一致（規格 5.6）。
    /// </summary>
    private string Build(string calendarName, IReadOnlyList<EventOccurrence> rows)
    {
        var stamp = IcsWriter.Utc(_clock.GetUtcNow().UtcDateTime);
        var sb = new StringBuilder();

        IcsWriter.AppendFolded(sb, "BEGIN:VCALENDAR");
        IcsWriter.AppendFolded(sb, "VERSION:2.0");
        IcsWriter.AppendFolded(sb, "PRODID:-//OfficeCal//Meeting Room Booking//ZH-TW");
        IcsWriter.AppendFolded(sb, "CALSCALE:GREGORIAN");
        IcsWriter.AppendFolded(sb, "METHOD:PUBLISH");
        IcsWriter.AppendFolded(sb, $"X-WR-CALNAME:{IcsWriter.Escape(calendarName)}");
        IcsWriter.AppendFolded(sb, "X-WR-TIMEZONE:Asia/Taipei");
        sb.Append(IcsWriter.TaipeiVTimeZone());

        foreach (var o in rows.Where(o => !o.IsCancelled
                                          && o.Event?.Status != EventStatus.Cancelled))
        {
            IcsWriter.AppendFolded(sb, "BEGIN:VEVENT");
            IcsWriter.AppendFolded(sb, $"UID:{o.Id}@{UidDomain}");
            IcsWriter.AppendFolded(sb, $"DTSTAMP:{stamp}");
            IcsWriter.AppendFolded(sb,
                $"DTSTART;TZID=Asia/Taipei:{IcsWriter.Local(o.StartAt)}");
            IcsWriter.AppendFolded(sb,
                $"DTEND;TZID=Asia/Taipei:{IcsWriter.Local(o.EndAt)}");
            IcsWriter.AppendFolded(sb,
                $"SUMMARY:{IcsWriter.Escape(o.TitleOverride ?? o.Event?.Title ?? "")}");

            if (!string.IsNullOrWhiteSpace(o.Event?.Description))
                IcsWriter.AppendFolded(sb, $"DESCRIPTION:{IcsWriter.Escape(o.Event.Description)}");

            if (o.Room is not null)
                IcsWriter.AppendFolded(sb,
                    $"LOCATION:{IcsWriter.Escape(o.Room.Name + (o.Room.Location is null ? "" : $"（{o.Room.Location}）"))}");

            IcsWriter.AppendFolded(sb, "END:VEVENT");
        }

        IcsWriter.AppendFolded(sb, "END:VCALENDAR");
        return sb.ToString();
    }
}
