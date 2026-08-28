namespace OfficeCal.Core.Exceptions;

/// <summary>輸入不合法 → HTTP 400。</summary>
public class ValidationException : Exception
{
    public List<string> Errors { get; } = new();
    public ValidationException(string message) : base(message) { }
    public ValidationException(string message, IEnumerable<string> errors) : base(message)
        => Errors.AddRange(errors);
}

/// <summary>查無資料 → HTTP 404。</summary>
public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}

/// <summary>權限不足 → HTTP 403。</summary>
public class ForbiddenException : Exception
{
    public ForbiddenException(string message) : base(message) { }
}

/// <summary>會議廳時段衝突 → HTTP 409，回應的 data 帶 conflicts 明細。</summary>
public class ConflictException : Exception
{
    public IReadOnlyList<ConflictDetail> Conflicts { get; }
    public ConflictException(string message, IReadOnlyList<ConflictDetail> conflicts) : base(message)
        => Conflicts = conflicts;
}

/// <summary>規格 7.2 的 conflicts 陣列元素。</summary>
public class ConflictDetail
{
    public int OccurrenceId { get; set; }
    public string RoomName { get; set; } = "";
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public string OwnerName { get; set; } = "";
    public string Title { get; set; } = "";
}
