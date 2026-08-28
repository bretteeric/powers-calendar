namespace OfficeCal.Core.Enums;

public enum UserRole { Employee = 0, Admin = 1 }

public enum EventStatus { Active = 0, Cancelled = 1 }

public enum NotificationType
{
    AddedToEvent = 0,
    EventUpdated = 1,
    EventCancelled = 2,
    ForcedCancellation = 3,
}

public enum RecurrenceFrequency { Daily = 0, Weekly = 1, Monthly = 2, Yearly = 3 }

/// <summary>每月重複的兩種模式：每月 N 日 / 每月第 N 個星期 X。</summary>
public enum MonthlyMode { DayOfMonth = 0, WeekDayOfMonth = 1 }

public enum RecurrenceEndMode { UntilDate = 0, Count = 1 }

public enum CalendarScope { Me = 0, Room = 1, All = 2 }

public enum EditMode { Single = 0, Series = 1 }
