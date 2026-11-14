using System.Buffers;
using System.Globalization;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Services;

public partial class LogParserService : ILogParserService
{
    private const string LoggerPrefix = "[GovionLogger.cpp:";
    private const string PmntReadPrefix = "read value:PMNT";

    private static readonly SearchValues<string> RelevantKeywords = SearchValues.Create(
        ["PMNT", "PANELNAME:", "BLUNAME:", "OTP-", "Query", "MES debt collection"],
        StringComparison.Ordinal);

    public DateTime? ParseLogDateTime(string? text, bool allowCurrentCulture = false)
    {
        return text is null ? null : ParseLogDateTime(text.AsSpan(), allowCurrentCulture);
    }

    public static DateTime? ParseLogDateTime(ReadOnlySpan<char> text, bool allowCurrentCulture = false)
    {
        ReadOnlySpan<char> span = text.Trim();
        if (span.IsEmpty)
        {
            return null;
        }

        const DateTimeStyles styles = DateTimeStyles.None;

        if (HasIsoDatePrefix(span))
        {
            return DateTime.TryParse(span, CultureInfo.InvariantCulture, styles, out DateTime result)
                ? result
                : null;
        }

        if (allowCurrentCulture &&
            DateTime.TryParse(span, CultureInfo.CurrentCulture, styles, out DateTime currentResult))
        {
            return currentResult;
        }

        return null;
    }

    public UnitRecord ConvertLogBlockToUnit(IReadOnlyList<string> blockLines, int index = 0, string source = "")
    {
        if (blockLines == null || blockLines.Count == 0)
        {
            return new UnitRecord
            {
                Index = index,
                Source = source
            };
        }

        string pmnt = string.Empty;
        string panelName = string.Empty;
        string bluName = string.Empty;
        var errors = new List<string>();
        DateTime? timestamp = null;
        bool? queryOk = null;
        bool? debtOk = null;

        foreach (string line in blockLines)
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            ReadOnlySpan<char> span = line.AsSpan();

            if (timestamp == null && TryExtractBracketTimestamp(span, out ReadOnlySpan<char> timestampSpan))
            {
                timestamp = ParseLogDateTime(timestampSpan);
            }

            // Single-pass SIMD pre-filter: skip lines that contain none of the target keywords
            if (span.IndexOfAny(RelevantKeywords) < 0)
            {
                continue;
            }

            if (!TryExtractGovionLogContent(span, out ReadOnlySpan<char> content))
            {
                continue;
            }

            if (TryExtractPmnt(content, out string? parsedPmnt))
            {
                pmnt = parsedPmnt;
            }

            int panelIdx = content.IndexOf("PANELNAME:", StringComparison.Ordinal);
            if (panelIdx >= 0 && panelIdx + 10 < content.Length)
            {
                panelName = content[(panelIdx + 10)..].Trim().ToString();
            }

            int bluIdx = content.IndexOf("BLUNAME:", StringComparison.Ordinal);
            if (bluIdx >= 0 && bluIdx + 8 < content.Length)
            {
                bluName = content[(bluIdx + 8)..].Trim().ToString();
            }

            if (content.Contains("Query", StringComparison.Ordinal))
            {
                bool? qStatus = MatchStatusKeyword(content, "Query");
                if (qStatus.HasValue)
                {
                    queryOk = qStatus.Value;
                }
            }

            if (content.Contains("MES debt collection", StringComparison.Ordinal))
            {
                bool? dStatus = MatchStatusKeyword(content, "MES debt collection");
                if (dStatus.HasValue)
                {
                    debtOk = dStatus.Value;
                }
            }

            string? otpDefect = TryExtractOtpDefect(content);
            if (otpDefect is not null && !errors.Contains(otpDefect))
            {
                errors.Add(otpDefect);
            }
        }

        string mesResult = string.Empty;
        if (queryOk == false)
        {
            mesResult = "Fail (Query PMNT)";
        }
        else if (debtOk == false)
        {
            mesResult = "Fail (MES Debt Collection)";
        }
        else if (queryOk == true || debtOk == true)
        {
            mesResult = "Successful";
        }

        return new UnitRecord
        {
            Index = index,
            Source = source,
            Pmnt = pmnt,
            PassCount = 0,
            TotalRuns = 0,
            PanelName = panelName,
            BluName = bluName,
            Errors = errors,
            Timestamp = timestamp,
            MesResult = mesResult
        };
    }

    private static bool HasIsoDatePrefix(ReadOnlySpan<char> span)
    {
        // Matches ^\d{4}-\d{1,2}-\d{1,2}
        if (span.Length < 8 ||
            !IsAsciiDigits(span, 0, 4) ||
            span[4] != '-')
        {
            return false;
        }

        int pos = 5;
        int monthDigits = CountAsciiDigits(span, pos, 2);
        if (monthDigits == 0)
        {
            return false;
        }

        pos += monthDigits;
        if (pos >= span.Length || span[pos] != '-')
        {
            return false;
        }

        pos++;
        return CountAsciiDigits(span, pos, 2) >= 1;
    }

    private static bool TryExtractBracketTimestamp(ReadOnlySpan<char> span, out ReadOnlySpan<char> timestampSpan)
    {
        // Matches \[(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?)\]
        ReadOnlySpan<char> remaining = span;
        while (true)
        {
            int openIdx = remaining.IndexOf('[');
            if (openIdx < 0)
            {
                break;
            }

            remaining = remaining[(openIdx + 1)..];
            if (remaining.Length < 20)
            {
                continue;
            }

            if (IsAsciiDigits(remaining, 0, 4) &&
                remaining[4] == '-' &&
                IsAsciiDigits(remaining, 5, 2) &&
                remaining[7] == '-' &&
                IsAsciiDigits(remaining, 8, 2) &&
                char.IsWhiteSpace(remaining[10]))
            {
                int pos = 11;
                while (pos < remaining.Length && char.IsWhiteSpace(remaining[pos]))
                {
                    pos++;
                }

                if (pos + 8 < remaining.Length &&
                    IsAsciiDigits(remaining, pos, 2) &&
                    remaining[pos + 2] == ':' &&
                    IsAsciiDigits(remaining, pos + 3, 2) &&
                    remaining[pos + 5] == ':' &&
                    IsAsciiDigits(remaining, pos + 6, 2))
                {
                    int endPos = pos + 8;
                    if (endPos < remaining.Length && remaining[endPos] == '.')
                    {
                        int fracStart = endPos + 1;
                        int fracEnd = fracStart;
                        while (fracEnd < remaining.Length && char.IsAsciiDigit(remaining[fracEnd]))
                        {
                            fracEnd++;
                        }

                        if (fracEnd == fracStart)
                        {
                            continue;
                        }

                        endPos = fracEnd;
                    }

                    if (endPos < remaining.Length && remaining[endPos] == ']')
                    {
                        timestampSpan = remaining[..endPos];
                        return true;
                    }
                }
            }
        }

        timestampSpan = default;
        return false;
    }

    private static bool TryExtractGovionLogContent(ReadOnlySpan<char> span, out ReadOnlySpan<char> content)
    {
        // Matches \[GovionLogger\.cpp:\d+\]\s+(.+)$
        ReadOnlySpan<char> remaining = span;
        while (true)
        {
            int markerIdx = remaining.IndexOf(LoggerPrefix, StringComparison.Ordinal);
            if (markerIdx < 0)
            {
                break;
            }

            remaining = remaining[(markerIdx + LoggerPrefix.Length)..];
            int digitCount = 0;
            while (digitCount < remaining.Length && char.IsAsciiDigit(remaining[digitCount]))
            {
                digitCount++;
            }

            if (digitCount == 0 ||
                digitCount >= remaining.Length ||
                remaining[digitCount] != ']')
            {
                continue;
            }

            int afterBracket = digitCount + 1;
            if (afterBracket >= remaining.Length || !char.IsWhiteSpace(remaining[afterBracket]))
            {
                continue;
            }

            int contentStart = afterBracket + 1;
            while (contentStart < remaining.Length && char.IsWhiteSpace(remaining[contentStart]))
            {
                contentStart++;
            }

            // Preserve original regex behavior where trailing whitespace allows \s+(.+) to capture at least 1 char before Trim()
            if (contentStart <= remaining.Length && afterBracket < remaining.Length)
            {
                content = remaining[contentStart..].Trim();
                return !content.IsEmpty;
            }
        }

        content = default;
        return false;
    }

    private static bool TryExtractPmnt(ReadOnlySpan<char> content, out string pmnt)
    {
        ReadOnlySpan<char> remaining = content;
        while (true)
        {
            int idx = remaining.IndexOf(PmntReadPrefix, StringComparison.Ordinal);
            if (idx < 0)
            {
                break;
            }

            int pmntStart = idx + 11; // after "read value:"
            int pos = idx + PmntReadPrefix.Length;
            int digitStart = pos;
            while (pos < remaining.Length && char.IsAsciiDigit(remaining[pos]))
            {
                pos++;
            }

            if (pos > digitStart && pos < remaining.Length && (remaining[pos] == 'A' || remaining[pos] == 'B'))
            {
                pmnt = remaining[pmntStart..(pos + 1)].ToString();
                return true;
            }

            remaining = remaining[(idx + PmntReadPrefix.Length)..];
        }

        pmnt = string.Empty;
        return false;
    }

    private static bool? MatchStatusKeyword(ReadOnlySpan<char> content, string keyword)
    {
        // Priority 1: <keyword>\s+successful
        ReadOnlySpan<char> remaining = content;
        int firstKeywordEnd = -1;

        while (true)
        {
            int idx = remaining.IndexOf(keyword, StringComparison.Ordinal);
            if (idx < 0)
            {
                break;
            }

            int afterKw = idx + keyword.Length;
            if (firstKeywordEnd < 0)
            {
                firstKeywordEnd = (content.Length - remaining.Length) + afterKw;
            }

            if (afterKw < remaining.Length && char.IsWhiteSpace(remaining[afterKw]))
            {
                int pos = afterKw + 1;
                while (pos < remaining.Length && char.IsWhiteSpace(remaining[pos]))
                {
                    pos++;
                }

                if (remaining[pos..].StartsWith("successful", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            remaining = remaining[afterKw..];
        }

        // Priority 2: <keyword>.*fail
        if (firstKeywordEnd >= 0 && content[firstKeywordEnd..].Contains("fail", StringComparison.Ordinal))
        {
            return false;
        }

        return null;
    }

    private static string? TryExtractOtpDefect(ReadOnlySpan<char> content)
    {
        ReadOnlySpan<char> remaining = content;
        while (true)
        {
            int idx = remaining.IndexOf("OTP-", StringComparison.Ordinal);
            if (idx < 0)
            {
                break;
            }

            ReadOnlySpan<char> afterDash = remaining[(idx + 4)..];
            string? candidate = null;
            if (afterDash.StartsWith("POWERON", StringComparison.Ordinal))
            {
                candidate = "POWERON";
            }
            else if (afterDash.StartsWith("POWEROFF", StringComparison.Ordinal))
            {
                candidate = "POWEROFF";
            }
            else if (afterDash.StartsWith("XYLV", StringComparison.Ordinal))
            {
                candidate = "XYLV";
            }
            else if (afterDash.StartsWith("FLICKER", StringComparison.Ordinal))
            {
                candidate = "FLICKER";
            }

            if (candidate is not null)
            {
                int pos = candidate.Length;
                if (pos < afterDash.Length && char.IsWhiteSpace(afterDash[pos]))
                {
                    pos++;
                    while (pos < afterDash.Length && char.IsWhiteSpace(afterDash[pos]))
                    {
                        pos++;
                    }

                    if (afterDash[pos..].StartsWith("NG", StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
            }

            remaining = remaining[(idx + 4)..];
        }

        return null;
    }

    private static bool IsAsciiDigits(ReadOnlySpan<char> span, int start, int count)
    {
        if (start + count > span.Length)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (!char.IsAsciiDigit(span[start + i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int CountAsciiDigits(ReadOnlySpan<char> span, int start, int maxCount)
    {
        int count = 0;
        while (count < maxCount && start + count < span.Length && char.IsAsciiDigit(span[start + count]))
        {
            count++;
        }

        return count;
    }
}
