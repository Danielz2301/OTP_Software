using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Interfaces;

/// <summary>
/// Pure predicate operations for evaluating two-stage unit filtering (Scope evaluation & Status presentation).
/// </summary>
public interface IUnitFilterService
{
    bool TextContains(string? text, string? pattern);
    bool IsWithinTimeRange(UnitRecord unit, DateTime? start, DateTime? endTime);
    bool IsUnitInScope(UnitRecord unit, UnitFilterSpec spec);
    bool IsUnitDisplayed(UnitRecord unit, UnitFilterSpec spec);
}

