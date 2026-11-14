using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Interfaces;

/// <summary>
/// Parses log timestamps and converts single inspection log blocks (START*** to END***) into UnitRecord domain objects.
/// </summary>
public interface ILogParserService
{
    DateTime? ParseLogDateTime(string? text, bool allowCurrentCulture = false);
    UnitRecord ConvertLogBlockToUnit(IReadOnlyList<string> blockLines, int index = 0, string source = "");
}

