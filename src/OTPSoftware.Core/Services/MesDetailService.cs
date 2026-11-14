using System.Buffers;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using OTPSoftware.Core.Caching;
using OTPSoftware.Core.Helpers;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Services;

/// <summary>
/// Reverse chunked stream parser for MES_1.log XML messages and optical measurement specification evaluator.
/// Employs backwards chunked file streaming with overlap to avoid loading large log files into memory,
/// self-invalidating LRU cache (capacity 100) keyed by file length and timestamp, and full Lua spec evaluation.
/// </summary>
public sealed partial class MesDetailService : IMesDetailService
{
    public sealed record CacheEntry(
        string PrimaryStamp,
        string SourceFile,
        string SourceStamp,
        IReadOnlyList<MesPanelDetailRecord> Results
    );

    private sealed record RawMesHit(
        string? CodeRaw,
        string? ValRaw,
        string OtpDefectCode,
        string OtpDefectValue,
        string TestResult,
        string MesResult,
        string RoiLocation,
        string DefectCodes,
        string TxTime,
        string LogTime
    );

    private sealed record RangeRule(double? Min, double? Max, string SpecDisplay);

    private static readonly Dictionary<string, string> ParameterAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["whitelv"] = "White_lv",
        ["colortemp"] = "cct",
        ["flicker"] = "flk"
    };

    private static readonly Dictionary<string, RangeRule> DefaultRangeRules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["White_lv"] = new(300.0, null, ">= 300.000"),
        ["whitex"] = new(0.288, 0.338, "0.288 ~ 0.338"),
        ["whitey"] = new(0.304, 0.354, "0.304 ~ 0.354"),
        ["cct"] = new(5500.0, 8000.0, "5500 ~ 8000"),
        ["redx"] = new(0.645, 0.695, "0.645 ~ 0.695"),
        ["redy"] = new(0.292, 0.342, "0.292 ~ 0.342"),
        ["greenx"] = new(0.272, 0.322, "0.272 ~ 0.322"),
        ["greeny"] = new(0.585, 0.635, "0.585 ~ 0.635"),
        ["bluex"] = new(0.116, 0.166, "0.116 ~ 0.166"),
        ["bluey"] = new(0.035, 0.085, "0.035 ~ 0.085"),
        ["flk"] = new(0.0, 30.0, "0.0 ~ 30.0")
    };

    private static readonly HashSet<string> ReferenceParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Red_LV", "Green_LV", "Blue_LV", "Black_LV", "L0_LV", "measured_area"
    };

    private static readonly HashSet<string> ResultParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "opticsresult", "afresult", "result", "testresult"
    };

    private readonly LruMemoryCache<string, CacheEntry> _cache = new(capacity: 100, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RangeRule> _rangeRules;
    private readonly double _contrastMin = 1050.0;
    private readonly string _contrastSpec = ">= 1050";
    private readonly double _ntscMinRatio = 0.42;
    private readonly double _ntscMinPct = 42.0;
    private readonly string _ntscSpec = ">= 0.42 (or >= 42%)";

    public MesDetailService(IReadOnlyDictionary<string, MesParameterSpec>? customSpecs = null)
    {
        if (customSpecs is null || customSpecs.Count == 0)
        {
            _rangeRules = DefaultRangeRules;
            return;
        }

        var merged = new Dictionary<string, RangeRule>(DefaultRangeRules, StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, spec) in customSpecs)
        {
            if (string.IsNullOrWhiteSpace(rawKey) || spec is null)
                continue;

            string canonical = CanonicalizeName(rawKey.AsSpan().Trim());
            string key = canonical.Length > 0 ? canonical : rawKey.Trim();
            if (key.Equals("contrast", StringComparison.OrdinalIgnoreCase))
            {
                if (spec.Min is { } minVal && double.IsFinite(minVal))
                {
                    _contrastMin = minVal;
                    _contrastSpec = !string.IsNullOrWhiteSpace(spec.SpecDisplay)
                        ? spec.SpecDisplay
                        : $">= {_contrastMin.ToString(CultureInfo.InvariantCulture)}";
                }
                else if (!string.IsNullOrWhiteSpace(spec.SpecDisplay))
                {
                    _contrastSpec = spec.SpecDisplay;
                }
                continue;
            }

            if (key.Equals("ntsc", StringComparison.OrdinalIgnoreCase))
            {
                if (spec.Min is { } minVal && double.IsFinite(minVal) && minVal > 0.0)
                {
                    if (minVal > 1.0)
                    {
                        _ntscMinPct = minVal;
                        _ntscMinRatio = minVal / 100.0;
                    }
                    else
                    {
                        _ntscMinRatio = minVal;
                        _ntscMinPct = minVal * 100.0;
                    }

                    _ntscSpec = !string.IsNullOrWhiteSpace(spec.SpecDisplay)
                        ? spec.SpecDisplay
                        : $">= {_ntscMinRatio.ToString(CultureInfo.InvariantCulture)} (or >= {_ntscMinPct.ToString(CultureInfo.InvariantCulture)}%)";
                }
                else if (!string.IsNullOrWhiteSpace(spec.SpecDisplay))
                {
                    _ntscSpec = spec.SpecDisplay;
                }
                continue;
            }

            DefaultRangeRules.TryGetValue(key, out var fallback);
            double? min = spec.Min is { } customMin && double.IsFinite(customMin) ? customMin : fallback?.Min;
            double? max = spec.Max is { } customMax && double.IsFinite(customMax) ? customMax : fallback?.Max;
            string display = !string.IsNullOrWhiteSpace(spec.SpecDisplay)
                ? spec.SpecDisplay
                : (fallback is not null && min == fallback.Min && max == fallback.Max
                    ? fallback.SpecDisplay
                    : FormatSpecRange(min, max));

            merged[key] = new RangeRule(min, max, display);
        }

        _rangeRules = merged;
    }

    private static string CanonicalizeName(ReadOnlySpan<char> name) =>
        ParameterAliases.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out string? canonical)
            ? canonical
            : string.Empty;

    private static string FormatSpecRange(double? min, double? max)
    {
        if (min.HasValue && max.HasValue)
        {
            return $"{min.Value.ToString(CultureInfo.InvariantCulture)} ~ {max.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        if (min.HasValue)
        {
            return $">= {min.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        if (max.HasValue)
        {
            return $"<= {max.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return "-";
    }

    public LruMemoryCache<string, CacheEntry> Cache => _cache;

    [GeneratedRegex(@"<OTPDEFECTCODE>(.*?)</OTPDEFECTCODE>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex OtpDefectCodeRegex();

    [GeneratedRegex(@"<OTPDEFECTVALUE>(.*?)</OTPDEFECTVALUE>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex OtpDefectValueRegex();

    [GeneratedRegex(@"<TESTRESULT>(.*?)</TESTRESULT>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TestResultRegex();

    [GeneratedRegex(@"<ROILOCATION>(.*?)</ROILOCATION>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RoiLocationRegex();

    [GeneratedRegex(@"<DEFECTCODES>(.*?)</DEFECTCODES>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DefectCodesRegex();

    [GeneratedRegex(@"<TRANSACTIONSTARTTIME>(.*?)</TRANSACTIONSTARTTIME>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TransactionStartTimeRegex();

    [GeneratedRegex(@"<RETURNCODE>(.*?)</RETURNCODE>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ReturnCodeRegex();

    [GeneratedRegex(@"<RETURNMESSAGE>(.*?)</RETURNMESSAGE>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ReturnMessageRegex();

    [GeneratedRegex(@"\[(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]")]
    private static partial Regex LogTimeRegex();

    private static string EvaluateRange(ReadOnlySpan<char> valStr, bool isNum, bool isNonFinite, bool inRange, bool groupIsNg)
    {
        if (isNonFinite) return "NG";
        if (isNum) return inRange ? "OK" : "NG";
        if (valStr.Equals("NG", StringComparison.OrdinalIgnoreCase)) return "NG";
        if (valStr.Equals("OK", StringComparison.OrdinalIgnoreCase)) return "OK";
        return groupIsNg ? "NG" : "OK";
    }

    public MesParameterEvaluation EvaluateParameter(string? paramName, string? paramValue, bool groupIsNg = false)
    {
        return EvaluateParameter(
            paramName is null ? default : paramName.AsSpan(),
            paramValue is null ? default : paramValue.AsSpan(),
            groupIsNg);
    }

    public MesParameterEvaluation EvaluateParameter(ReadOnlySpan<char> paramName, ReadOnlySpan<char> paramValue, bool groupIsNg = false)
    {
        ReadOnlySpan<char> rawName = paramName.Trim();
        ReadOnlySpan<char> name = ParameterAliases.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(rawName, out string? canonical)
            ? canonical.AsSpan()
            : rawName;
        ReadOnlySpan<char> valStr = paramValue.Trim();
        string spec = "-";
        string status = "OK";

        bool parsedNum = double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double num);
        bool isNonFinite = parsedNum && !double.IsFinite(num);
        bool isNum = parsedNum && double.IsFinite(num);

        if (_rangeRules.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out var rule))
        {
            spec = rule.SpecDisplay;
            bool inRange = (!rule.Min.HasValue || num >= rule.Min.Value) &&
                           (!rule.Max.HasValue || num <= rule.Max.Value);
            status = EvaluateRange(valStr, isNum, isNonFinite, inRange, groupIsNg);
        }
        else if (name.Equals("contrast", StringComparison.OrdinalIgnoreCase))
        {
            spec = _contrastSpec;
            if (isNonFinite) status = "NG";
            else if (isNum)
            {
                status = num <= 0.0 ? "N/A" : (num < _contrastMin ? "NG" : "OK");
            }
            else if (valStr.Equals("NG", StringComparison.OrdinalIgnoreCase)) status = "NG";
            else if (valStr.Equals("OK", StringComparison.OrdinalIgnoreCase)) status = "OK";
            else status = "N/A";
        }
        else if (name.Equals("ntsc", StringComparison.OrdinalIgnoreCase))
        {
            spec = _ntscSpec;
            bool hasPercent = valStr.EndsWith('%');
            double pctNum = num;
            bool isPctNum = isNum;
            if (hasPercent)
            {
                ReadOnlySpan<char> bare = valStr[..^1].Trim();
                bool parsedPct = double.TryParse(bare, NumberStyles.Float, CultureInfo.InvariantCulture, out pctNum);
                isNonFinite = parsedPct && !double.IsFinite(pctNum);
                isPctNum = parsedPct && double.IsFinite(pctNum);
            }

            if (isNonFinite) status = "NG";
            else if (isPctNum)
            {
                if (pctNum <= 0.0) status = "N/A";
                else if (hasPercent) status = pctNum < _ntscMinPct ? "NG" : "OK";
                else if (pctNum < _ntscMinRatio || (pctNum > 1.0 && pctNum < _ntscMinPct)) status = "NG";
                else status = "OK";
            }
            else if (valStr.Equals("NG", StringComparison.OrdinalIgnoreCase)) status = "NG";
            else if (valStr.Equals("OK", StringComparison.OrdinalIgnoreCase)) status = "OK";
            else status = "N/A";
        }
        else if (ReferenceParameters.GetAlternateLookup<ReadOnlySpan<char>>().Contains(rawName))
        {
            spec = "Ref";
            status = valStr.Equals("NG", StringComparison.OrdinalIgnoreCase) ? "NG" : "OK";
        }
        else if (ResultParameters.GetAlternateLookup<ReadOnlySpan<char>>().Contains(rawName))
        {
            spec = "OK";
            status = valStr.Equals("OK", StringComparison.OrdinalIgnoreCase)
                ? "OK"
                : (valStr.Equals("NG", StringComparison.OrdinalIgnoreCase) || groupIsNg ? "NG" : "OK");
        }
        else
        {
            spec = "-";
            if (valStr.Equals("NG", StringComparison.OrdinalIgnoreCase)) status = "NG";
            else if (valStr.Equals("OK", StringComparison.OrdinalIgnoreCase)) status = "OK";
            else status = groupIsNg ? "NG" : "OK";
        }

        MesEvaluationStatus evalStatus = status switch
        {
            "NG" => MesEvaluationStatus.Ng,
            "OK" => MesEvaluationStatus.Ok,
            _ => MesEvaluationStatus.Neutral
        };

        return new MesParameterEvaluation(spec, status, evalStatus);
    }

    public IReadOnlyList<MesParameterRow> ConvertFromOtpDefectDetail(string? otpDefectCode, string? otpDefectValue)
    {
        var rows = new List<MesParameterRow>();
        ReadOnlySpan<char> cleanCode = otpDefectCode is null ? default : otpDefectCode.AsSpan().Trim().Trim("[]").Trim();
        ReadOnlySpan<char> cleanVal = otpDefectValue is null ? default : otpDefectValue.AsSpan().Trim().Trim("[]").Trim();

        if (cleanCode.IsWhiteSpace() && cleanVal.IsWhiteSpace())
        {
            rows.Add(new MesParameterRow(1, "(Empty)", "OTPDEFECTVALUE", "[]", "-", "N/A", MesEvaluationStatus.Neutral));
            return rows;
        }

        var codeReader = new PipeSectionReader(cleanCode);
        var valReader = new PipeSectionReader(cleanVal);

        int idx = 1;
        int sectionIndex = 0;
        while (codeReader.HasMore || valReader.HasMore)
        {
            ReadOnlySpan<char> rawCodeSeg = codeReader.Next().Trim();
            ReadOnlySpan<char> sectionSpan = valReader.Next().Trim();

            bool groupIsNg = false;
            string groupCode;
            if (rawCodeSeg.IsEmpty)
            {
                groupCode = $"OTP-GROUP-{sectionIndex + 1}";
            }
            else
            {
                groupIsNg = rawCodeSeg.Equals("NG", StringComparison.OrdinalIgnoreCase) ||
                            rawCodeSeg.EndsWith("-NG", StringComparison.OrdinalIgnoreCase);

                bool hasOkOrNgSuffix = rawCodeSeg.EndsWith("-OK", StringComparison.OrdinalIgnoreCase) ||
                                       rawCodeSeg.EndsWith("-NG", StringComparison.OrdinalIgnoreCase);
                groupCode = hasOkOrNgSuffix ? rawCodeSeg[..^3].ToString() : rawCodeSeg.ToString();
            }

            sectionIndex++;

            if (sectionSpan.IsWhiteSpace())
            {
                rows.Add(new MesParameterRow(
                    idx++,
                    groupCode,
                    "(No parameters)",
                    "-",
                    "-",
                    groupIsNg ? "NG" : "OK",
                    groupIsNg ? MesEvaluationStatus.Ng : MesEvaluationStatus.Ok
                ));
                continue;
            }

            foreach (Range pairRange in sectionSpan.Split(','))
            {
                ReadOnlySpan<char> trimmed = sectionSpan[pairRange].Trim();
                if (trimmed.IsEmpty) continue;

                int eqPos = trimmed.IndexOf('=');
                ReadOnlySpan<char> pNameSpan;
                ReadOnlySpan<char> pValSpan;
                if (eqPos > 0)
                {
                    pNameSpan = trimmed[..eqPos].Trim();
                    pValSpan = trimmed[(eqPos + 1)..].Trim();
                }
                else
                {
                    pNameSpan = trimmed;
                    pValSpan = default;
                }

                var eval = EvaluateParameter(pNameSpan, pValSpan, groupIsNg);
                rows.Add(new MesParameterRow(
                    idx++,
                    groupCode,
                    pNameSpan.ToString(),
                    pValSpan.IsEmpty ? string.Empty : pValSpan.ToString(),
                    eval.Spec,
                    eval.StatusText,
                    eval.EvaluationStatus
                ));
            }
        }

        return rows;
    }

    private ref struct PipeSectionReader(ReadOnlySpan<char> span)
    {
        private ReadOnlySpan<char> _remaining = span;
        public bool HasMore { get; private set; } = !span.IsWhiteSpace();

        public ReadOnlySpan<char> Next()
        {
            if (!HasMore)
            {
                return default;
            }

            int idx = _remaining.IndexOf('|');
            if (idx < 0)
            {
                HasMore = false;
                return _remaining;
            }

            ReadOnlySpan<char> segment = _remaining[..idx];
            _remaining = _remaining[(idx + 1)..];
            return segment;
        }
    }

    private static bool TryGetFileStamp(FileInfo info, out long length, out string stamp)
    {
        length = 0;
        stamp = string.Empty;
        try
        {
            if (!info.Exists)
                return false;

            length = info.Length;
            stamp = $"{length}|{info.LastWriteTimeUtc.Ticks}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public IReadOnlyList<MesPanelDetailRecord> FindMesPanelDetails(
        string mesLogPath,
        string panelName,
        int maxRotatedFiles = 5,
        long chunkBytes = 4 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var results = new List<MesPanelDetailRecord>();
        if (string.IsNullOrWhiteSpace(mesLogPath) || string.IsNullOrWhiteSpace(panelName))
            return results;

        string targetPanel = panelName.Trim();
        if (string.IsNullOrEmpty(targetPanel))
            return results;

        cancellationToken.ThrowIfCancellationRequested();

        var resolvedFamily = LogFileFamilyHelper.ResolveLogFileFamily(
            mesLogPath,
            maxRotatedFiles,
            includeFirstTimestamps: false);

        string activeLookupPath = LogFileFamilyHelper.ResolveActiveLogPath(mesLogPath);
        var primaryInfo = new FileInfo(activeLookupPath);
        if (!primaryInfo.Exists && !string.Equals(activeLookupPath, mesLogPath, StringComparison.OrdinalIgnoreCase))
        {
            primaryInfo = new FileInfo(mesLogPath);
        }

        var filesToSearch = new List<string>();
        if (resolvedFamily is { NewestToOldest.Count: > 0 })
        {
            foreach (var entry in resolvedFamily.NewestToOldest)
            {
                filesToSearch.Add(entry.Path);
            }
        }
        else if (primaryInfo.Exists)
        {
            filesToSearch.Add(primaryInfo.FullName);
        }

        if (filesToSearch.Count == 0)
            return results;

        FileInfo anchorInfo = primaryInfo.Exists ? primaryInfo : new FileInfo(filesToSearch[0]);
        if (!TryGetFileStamp(anchorInfo, out _, out string baseStamp))
            return results;

        string primaryStamp = $"{baseStamp}|{maxRotatedFiles}";
        string cacheKey = $"{anchorInfo.FullName}|{targetPanel}";

        if (_cache.TryGetValue(cacheKey, out var cachedEntry))
        {
            if (cachedEntry != null && cachedEntry.PrimaryStamp == primaryStamp)
            {
                bool sourceOk = true;
                if (!string.IsNullOrEmpty(cachedEntry.SourceFile) &&
                    !string.Equals(cachedEntry.SourceFile, anchorInfo.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    var srcInfo = new FileInfo(cachedEntry.SourceFile);
                    TryGetFileStamp(srcInfo, out _, out string srcStamp);
                    if (srcStamp != cachedEntry.SourceStamp)
                    {
                        sourceOk = false;
                    }
                }

                if (sourceOk)
                {
                    return cachedEntry.Results;
                }
            }
        }

        string needle = $"<PANELNAME>{targetPanel}</PANELNAME>";
        byte[]? lowerAsciiNeedle = Ascii.IsValid(needle)
            ? Encoding.ASCII.GetBytes(needle.ToLowerInvariant())
            : null;
        long effectiveChunk = Math.Max(64 * 1024, chunkBytes);
        const int headGuardBytes = 2048;
        const int overlapBytes = 32768;
        string matchedSourceFile = "";
        string matchedSourceStamp = "";

        foreach (string currentPath in filesToSearch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var curInfo = string.Equals(currentPath, anchorInfo.FullName, StringComparison.OrdinalIgnoreCase)
                ? anchorInfo
                : new FileInfo(currentPath);
            if (!TryGetFileStamp(curInfo, out long curLength, out string curStamp) || curLength <= 0)
                continue;

            var chunkGroups = new List<List<RawMesHit>>();
            const FileShare shareMode = FileShare.ReadWrite | FileShare.Delete;

            try
            {
                using var fs = new FileStream(currentPath, FileMode.Open, FileAccess.Read, shareMode);
                long fileLen = fs.Length;
                long chunkEnd = fileLen;
                long processedFromByte = fileLen + 1;
                int maxBufSize = (int)Math.Min(fileLen, effectiveChunk);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(maxBufSize);
                try
                {
                    while (chunkEnd > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        long chunkStart = Math.Max(0, chunkEnd - effectiveChunk);
                        int readLen = (int)(chunkEnd - chunkStart);

                        fs.Seek(chunkStart, SeekOrigin.Begin);
                        int got = 0;
                        while (got < readLen)
                        {
                            int n = fs.Read(buffer, got, readLen - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        if (got <= 0) break;

                        int decodeStart = 0;
                        if (chunkStart > 0)
                        {
                            while (decodeStart < 4 && decodeStart < got && (buffer[decodeStart] & 0xC0) == 0x80)
                            {
                                decodeStart++;
                            }
                        }

                        long minValidByte = chunkStart == 0 ? 0 : chunkStart + headGuardBytes;
                        long maxValidByte = processedFromByte;
                        long nextProcessedFromByte = minValidByte;

                        var chunkHits = new List<RawMesHit>();

                        if (lowerAsciiNeedle is not null)
                        {
                            ReadOnlySpan<byte> chunkSpan = buffer.AsSpan(decodeStart, got - decodeStart);
                            int searchPos = 0;
                            while (true)
                            {
                                int relIdx = IndexOfAsciiIgnoreCase(chunkSpan[searchPos..], lowerAsciiNeedle);
                                if (relIdx < 0) break;

                                int matchBytePos = searchPos + relIdx;
                                searchPos = matchBytePos + lowerAsciiNeedle.Length;

                                long absByte = chunkStart + decodeStart + matchBytePos;
                                if (absByte < minValidByte || absByte >= maxValidByte) continue;

                                int bodyEndRel = IndexOfAsciiIgnoreCase(chunkSpan[matchBytePos..], BodyEndTagLower);
                                if (bodyEndRel < 0)
                                {
                                    if (absByte >= nextProcessedFromByte)
                                    {
                                        nextProcessedFromByte = absByte + 1;
                                    }
                                    continue;
                                }

                                int bodyEndByte = matchBytePos + bodyEndRel;
                                if (bodyEndRel > 32768 || Encoding.UTF8.GetCharCount(chunkSpan[matchBytePos..bodyEndByte]) > 8000)
                                    continue;

                                int lineStartByte = chunkSpan[..matchBytePos].LastIndexOf((byte)'\n');
                                if (lineStartByte < 0) lineStartByte = 0;
                                if (chunkSpan[lineStartByte..matchBytePos].IndexOf("recv[LUATHREAD]"u8) >= 0)
                                    continue;

                                ReadOnlySpan<byte> bodySpanBytes = chunkSpan[matchBytePos..bodyEndByte];
                                if (IndexOfAsciiIgnoreCase(bodySpanBytes, OtpDefectCodeOpenLower) < 0 &&
                                    IndexOfAsciiIgnoreCase(bodySpanBytes, OtpDefectValueOpenLower) < 0)
                                {
                                    continue;
                                }

                                // Decode ONLY the small enclosing message slice (~1-9 KB) instead of the entire 4 MB buffer (zero LOH allocation).
                                string bodyChunk = Encoding.UTF8.GetString(bodySpanBytes);

                                string? returnChunk = null;
                                int msgTailLimit = Math.Min(chunkSpan.Length - bodyEndByte, 4000);
                                if (msgTailLimit > 0)
                                {
                                    ReadOnlySpan<byte> msgTailSearch = chunkSpan.Slice(bodyEndByte, msgTailLimit);
                                    int msgRelEnd = IndexOfAsciiIgnoreCase(msgTailSearch, MessageEndTagLower);
                                    if (msgRelEnd > 0 && Encoding.UTF8.GetCharCount(msgTailSearch[..msgRelEnd]) < 1000)
                                    {
                                        returnChunk = Encoding.UTF8.GetString(msgTailSearch[..msgRelEnd]);
                                    }
                                }

                                int lookbackByteStart = Math.Max(0, matchBytePos - 600);
                                while (lookbackByteStart < matchBytePos && lookbackByteStart > 0 && (chunkSpan[lookbackByteStart] & 0xC0) == 0x80)
                                {
                                    lookbackByteStart++;
                                }
                                string headerChunk = Encoding.UTF8.GetString(chunkSpan[lookbackByteStart..matchBytePos]);

                                chunkHits.Add(ExtractRawMesHit(bodyChunk, returnChunk, headerChunk));
                            }
                        }
                        else
                        {
                            // Fallback for non-ASCII panel identifiers where UTF-8 byte lengths differ under case folding.
                            string text = Encoding.UTF8.GetString(buffer, decodeStart, got - decodeStart);
                            int pos = 0;
                            int lastByteCountIdx = 0;
                            long runningByteOffset = 0;
                            while (true)
                            {
                                int idx = text.IndexOf(needle, pos, StringComparison.OrdinalIgnoreCase);
                                if (idx < 0) break;

                                pos = idx + needle.Length;
                                runningByteOffset += Encoding.UTF8.GetByteCount(text.AsSpan(lastByteCountIdx, idx - lastByteCountIdx));
                                lastByteCountIdx = idx;

                                long absByte = chunkStart + decodeStart + runningByteOffset;
                                if (absByte < minValidByte || absByte >= maxValidByte) continue;

                                int bodyEnd = text.IndexOf("</BODY>", idx, StringComparison.OrdinalIgnoreCase);
                                if (bodyEnd < 0)
                                {
                                    if (absByte >= nextProcessedFromByte)
                                    {
                                        nextProcessedFromByte = absByte + 1;
                                    }
                                    continue;
                                }
                                if ((bodyEnd - idx) > 8000) continue;

                                int lineStart = text.LastIndexOf('\n', idx);
                                if (lineStart < 0) lineStart = 0;
                                if (text.AsSpan(lineStart, idx - lineStart).Contains("recv[LUATHREAD]", StringComparison.Ordinal))
                                    continue;

                                string bodyChunk = text[idx..bodyEnd];
                                if (!bodyChunk.Contains("<OTPDEFECTCODE>", StringComparison.OrdinalIgnoreCase) &&
                                    !bodyChunk.Contains("<OTPDEFECTVALUE>", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                string? returnChunk = null;
                                int msgEnd = text.IndexOf("</MESSAGE>", bodyEnd, StringComparison.OrdinalIgnoreCase);
                                if (msgEnd > bodyEnd && (msgEnd - bodyEnd) < 1000)
                                {
                                    returnChunk = text[bodyEnd..msgEnd];
                                }

                                int lookbackStart = Math.Max(0, idx - 600);
                                string headerChunk = text.Substring(lookbackStart, idx - lookbackStart);
                                chunkHits.Add(ExtractRawMesHit(bodyChunk, returnChunk, headerChunk));
                            }
                        }

                        if (chunkHits.Count > 0)
                        {
                            chunkGroups.Add(chunkHits);
                        }

                        if (chunkStart <= 0) break;
                        processedFromByte = nextProcessedFromByte;
                        chunkEnd = Math.Min(fileLen, chunkStart + overlapBytes);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Transient share lock or file rotation between enumeration and read; try older rotated archive.
            }

            if (chunkGroups.Count > 0)
            {
                for (int g = chunkGroups.Count - 1; g >= 0; g--)
                {
                    foreach (var raw in chunkGroups[g])
                    {
                        if (results.Count > 0 &&
                            results[^1].OtpDefectCode == raw.OtpDefectCode &&
                            results[^1].OtpDefectValue == raw.OtpDefectValue)
                        {
                            if (!string.IsNullOrEmpty(raw.TxTime))
                            {
                                results[^1] = results[^1] with { LogTime = raw.TxTime };
                            }
                            if (raw.MesResult != "Successful")
                            {
                                results[^1] = results[^1] with { MesResult = raw.MesResult };
                            }
                            continue;
                        }

                        var entry = new MesPanelDetailRecord
                        {
                            PanelName = targetPanel,
                            LogTime = raw.LogTime,
                            TestResult = raw.TestResult,
                            MesResult = raw.MesResult,
                            RoiLocation = raw.RoiLocation,
                            DefectCodes = raw.DefectCodes,
                            OtpDefectCode = raw.OtpDefectCode,
                            OtpDefectValue = raw.OtpDefectValue,
                            SourceFile = currentPath,
                            Parameters = ConvertFromOtpDefectDetail(raw.CodeRaw, raw.ValRaw)
                        };
                        results.Add(entry);
                    }
                }

                if (results.Count > 0)
                {
                    matchedSourceFile = currentPath;
                    matchedSourceStamp = curStamp;
                    break;
                }
            }
        }

        var finalArray = results.ToArray();
        if (finalArray.Length > 0)
        {
            _cache.Set(
                cacheKey,
                new CacheEntry(
                    primaryStamp,
                    matchedSourceFile,
                    matchedSourceStamp,
                    finalArray
                )
            );
        }

        return finalArray;
    }

    private static ReadOnlySpan<byte> BodyEndTagLower => "</body>"u8;
    private static ReadOnlySpan<byte> MessageEndTagLower => "</message>"u8;
    private static ReadOnlySpan<byte> OtpDefectCodeOpenLower => "<otpdefectcode>"u8;
    private static ReadOnlySpan<byte> OtpDefectValueOpenLower => "<otpdefectvalue>"u8;

    private static RawMesHit ExtractRawMesHit(string bodyChunk, string? returnChunk, string headerChunk)
    {
        var codeMatch = OtpDefectCodeRegex().Match(bodyChunk);
        string? code = codeMatch.Success ? codeMatch.Groups[1].Value.Trim() : null;

        var valMatch = OtpDefectValueRegex().Match(bodyChunk);
        string? val = valMatch.Success ? valMatch.Groups[1].Value.Trim() : null;

        string testRes = TestResultRegex().Match(bodyChunk).Groups[1].Value.Trim();
        string roi = RoiLocationRegex().Match(bodyChunk).Groups[1].Value.Trim();
        string defectCds = DefectCodesRegex().Match(bodyChunk).Groups[1].Value.Trim();
        string txTime = TransactionStartTimeRegex().Match(bodyChunk).Groups[1].Value.Trim();

        string mesResult = "Successful";
        if (returnChunk is not null)
        {
            var retMatch = ReturnCodeRegex().Match(returnChunk);
            if (retMatch.Success)
            {
                string retCode = retMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(retCode) && retCode != "0")
                {
                    string retMsg = ReturnMessageRegex().Match(returnChunk).Groups[1].Value.Trim();
                    mesResult = !string.IsNullOrEmpty(retMsg)
                        ? $"Fail ({retMsg})"
                        : $"Fail (Code: {retCode})";
                }
            }
        }

        string logTime = txTime;
        if (string.IsNullOrEmpty(logTime) && headerChunk.Length > 0)
        {
            var tsMatches = LogTimeRegex().Matches(headerChunk);
            if (tsMatches.Count > 0)
            {
                logTime = tsMatches[^1].Groups[1].Value;
            }
        }

        return new RawMesHit(
            code,
            val,
            code ?? string.Empty,
            val ?? string.Empty,
            testRes,
            mesResult,
            roi,
            defectCds,
            txTime,
            logTime
        );
    }

    private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> lowerAsciiNeedle)
    {
        int needleLen = lowerAsciiNeedle.Length;
        if (needleLen == 0 || haystack.Length < needleLen)
            return -1;

        byte firstLower = lowerAsciiNeedle[0];
        bool isFirstLetter = (uint)(firstLower - 'a') <= ('z' - 'a');
        byte firstUpper = isFirstLetter ? (byte)(firstLower - 32) : firstLower;

        int searchOffset = 0;
        while (searchOffset <= haystack.Length - needleLen)
        {
            int rel = isFirstLetter
                ? haystack[searchOffset..].IndexOfAny(firstLower, firstUpper)
                : haystack[searchOffset..].IndexOf(firstLower);
            if (rel < 0)
                return -1;

            int pos = searchOffset + rel;
            if (pos > haystack.Length - needleLen)
                return -1;

            bool matched = true;
            for (int j = 1; j < needleLen; j++)
            {
                byte b = haystack[pos + j];
                if ((uint)(b - 'A') <= ('Z' - 'A'))
                {
                    b = (byte)(b + 32);
                }
                if (b != lowerAsciiNeedle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return pos;

            searchOffset = pos + 1;
        }

        return -1;
    }
}
