namespace OTPSoftware.Core.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;

/// <summary>
/// Evaluates two-stage unit filtering (Scope evaluation &amp; Status presentation).
/// Supports dynamic N-machine ordinal mapping while preserving legacy 1/2 -> MMAPI120/MMAPI110 defaults.
/// </summary>
public sealed class UnitFilterService : IUnitFilterService
{
    private readonly IReadOnlyList<MachineConfig>? _machines;

    public UnitFilterService(IReadOnlyList<MachineConfig>? machines = null)
    {
        _machines = machines;
    }

    public static string ResolveExpectedSource(string? sourceName, IReadOnlyList<MachineConfig>? machines = null)
    {
        if (string.IsNullOrEmpty(sourceName) ||
            string.Equals(sourceName, "0", StringComparison.Ordinal) ||
            sourceName.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> trimmedStart = sourceName.AsSpan().TrimStart();
        if (!trimmedStart.IsEmpty &&
            (char.IsAsciiDigit(trimmedStart[0]) || trimmedStart[0] == '+') &&
            int.TryParse(sourceName, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ordinal) &&
            ordinal >= 1)
        {
            if (machines is not null && ordinal <= machines.Count && !string.IsNullOrWhiteSpace(machines[ordinal - 1].Name))
            {
                return machines[ordinal - 1].Name;
            }

            return ordinal switch
            {
                1 => "MMAPI120",
                2 => "MMAPI110",
                _ => sourceName
            };
        }

        return sourceName;
    }
    public bool TextContains(string? text, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return true;
        }

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsWithinTimeRange(UnitRecord unit, DateTime? start, DateTime? endTime)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (start == null && endTime == null)
        {
            return true;
        }

        if (unit.Timestamp == null)
        {
            return true;
        }

        DateTime ts = unit.Timestamp.Value;
        if (start.HasValue && ts < start.Value)
        {
            return false;
        }

        if (endTime.HasValue && ts > endTime.Value)
        {
            return false;
        }

        return true;
    }

    internal IReadOnlyList<MachineConfig>? Machines => _machines;

    public bool IsUnitInScope(UnitRecord unit, UnitFilterSpec spec)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(spec);

        string expected = ResolveExpectedSource(spec.SourceName, _machines);
        return IsUnitInScope(unit, spec, expected);
    }

    public bool IsUnitInScope(UnitRecord unit, UnitFilterSpec spec, string resolvedExpectedSource)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(spec);

        if (resolvedExpectedSource.Length > 0 && !string.Equals(unit.Source, resolvedExpectedSource, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TextContains(unit.Pmnt, spec.PmntPattern))
        {
            return false;
        }

        if (!TextContains(unit.PanelName, spec.PanelPattern))
        {
            return false;
        }

        if (!IsWithinTimeRange(unit, spec.StartTime, spec.EndTime))
        {
            return false;
        }

        return true;
    }

    public bool IsUnitDisplayed(UnitRecord unit, UnitFilterSpec spec)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(spec);

        bool isNg = unit.IsNg;

        if (spec.Status == UnitStatusFilter.NgOnly && !isNg)
        {
            return false;
        }

        if (spec.Status == UnitStatusFilter.OkOnly && isNg)
        {
            return false;
        }

        return true;
    }
}
