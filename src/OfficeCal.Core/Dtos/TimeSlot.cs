namespace OfficeCal.Core.Dtos;

/// <summary>一段時間區間。展開結果與衝突檢查的通用單位。</summary>
public readonly record struct TimeSlot(DateTime Start, DateTime End);
