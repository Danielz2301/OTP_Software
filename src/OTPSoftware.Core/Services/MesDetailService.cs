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

    private const int MesRegexTimeoutMs = 250;

    [GeneratedRegex(@"<OTPDEFECTCODE>(.*?)</OTPDEFECTCODE>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: MesRegexTimeoutMs)]
    private static partial Regex OtpDefectCodeRegex();

    [GeneratedRegex(@"<OTPDEFECTVALUE>(.*?)</OTPDEFECTVALUE>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: MesRegexTimeoutMs)]
    private static partial Regex OtpDefectValueRegex();

    [GeneratedRegex(@"<TESTRESULT>(.*?)</TESTRESULT>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: MesRegexTimeoutMs)]
    private static partial Regex TestResultRegex();

    [GeneratedRegex(@"<ROILOCATION>(.*?)</ROILOCATION>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: MesRegexTimeoutMs)]
    private static partial Regex RoiLocationRegex();

    [GeneratedRegex(@"<DEFECTCODES>(.*?)</DEFECTCODES>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: MesRegexTimeoutMs)]
    private static partial Regex DefectCodesRegex();

    [GeneratedRegex(@"<TRANSACTIONSTARTTIME>(.*?)</TRANSACTIONSTARTTIME>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: MesRegexTimeoutMs)]
    private static partial Regex TransactionStartTimeRegex();

    [GeneratedRegex(@"<RETURNCODE>(.*?)</RETURNCODE>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: MesRegexTimeoutMs)]
    private static partial Regex ReturnCodeRegex();

    [GeneratedRegex(@"<RETURNMESSAGE>(.*?)</RETURNMESSAGE>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: MesRegexTimeoutMs)]
    private static partial Regex ReturnMessageRegex();

    [GeneratedRegex(@"\[(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: MesRegexTimeoutMs)]
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
        long localLength = 0;
        string localStamp = string.Empty;
        try
        {
            bool exists = FileIoRetryHelper.ExecuteWithSharingRetry(() =>
            {
                info.Refresh();
                if (!info.Exists)
                    return false;

                localLength = info.Length;
                localStamp = $"{localLength}|{info.LastWriteTimeUtc.Ticks}";
                return true;
            });

            length = localLength;
            stamp = localStamp;
            return exists;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            length = 0;
            stamp = string.Empty;
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
                using var fs = FileIoRetryHelper.OpenSharedReadStreamWithRetry(
                    currentPath,
                    shareMode,
                    bufferSize: 65536,
                    options: FileOptions.SequentialScan,
                    cancellationToken: cancellationToken);
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

                        ReadOnlySpan<byte> chunkSpan = buffer.AsSpan(decodeStart, got - decodeStart);
                        int searchPos = 0;
                        while (TryFindNextPanelMatch(chunkSpan, searchPos, lowerAsciiNeedle, targetPanel, out int matchBytePos, out int nextSearchPos))
                        {
                            searchPos = nextSearchPos;

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

                            if (TryExtractRawMesHit(bodyChunk, returnChunk, headerChunk) is { } hit)
                            {
                                chunkHits.Add(hit);
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

    public IReadOnlyList<MesPanelDetailRecord> ExtractRecentPanelRecords(
        string mesLogPath,
        int maxBytes = 8 * 1024 * 1024)
    {
        if (string.IsNullOrWhiteSpace(mesLogPath))
        {
            return [];
        }

        string activeLookupPath = LogFileFamilyHelper.ResolveActiveLogPath(mesLogPath);
        var info = new FileInfo(activeLookupPath);
        if (!info.Exists && !string.Equals(activeLookupPath, mesLogPath, StringComparison.OrdinalIgnoreCase))
        {
            info = new FileInfo(mesLogPath);
        }

        if (!TryGetFileStamp(info, out long fileLen, out string stamp) || fileLen <= 0)
        {
            return [];
        }

        string cacheKey = $"BULK_SPC|{info.FullName}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached is not null && cached.PrimaryStamp == stamp)
        {
            return cached.Results;
        }

        var results = new List<MesPanelDetailRecord>();
        int readLen = (int)Math.Min(fileLen, Math.Max(64 * 1024, maxBytes));
        long startOffset = fileLen - readLen;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(readLen);
        try
        {
            using var fs = FileIoRetryHelper.OpenSharedReadStreamWithRetry(
                info.FullName,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 65536,
                options: FileOptions.SequentialScan);
            fs.Seek(startOffset, SeekOrigin.Begin);
            int got = 0;
            while (got < readLen)
            {
                int n = fs.Read(buffer, got, readLen - got);
                if (n <= 0) break;
                got += n;
            }

            if (got <= 0)
            {
                return [];
            }

            int decodeStart = 0;
            if (startOffset > 0)
            {
                while (decodeStart < 4 && decodeStart < got && (buffer[decodeStart] & 0xC0) == 0x80)
                {
                    decodeStart++;
                }
            }

            ReadOnlySpan<byte> chunkSpan = buffer.AsSpan(decodeStart, got - decodeStart);
            int searchPos = 0;
            while (searchPos < chunkSpan.Length)
            {
                int relPanelOpen = IndexOfAsciiIgnoreCase(chunkSpan[searchPos..], PanelNameOpenLower);
                if (relPanelOpen < 0)
                {
                    break;
                }

                int panelOpenPos = searchPos + relPanelOpen;
                int panelContentStart = panelOpenPos + PanelNameOpenLower.Length;
                if (panelContentStart >= chunkSpan.Length)
                {
                    break;
                }

                int panelSearchWin = Math.Min(chunkSpan.Length - panelContentStart, 256);
                int relPanelClose = IndexOfAsciiIgnoreCase(chunkSpan.Slice(panelContentStart, panelSearchWin), PanelNameCloseLower);
                if (relPanelClose < 0)
                {
                    searchPos = panelContentStart;
                    continue;
                }

                int bodyEndRel = IndexOfAsciiIgnoreCase(chunkSpan[panelOpenPos..], BodyEndTagLower);
                if (bodyEndRel < 0 || bodyEndRel > 32768)
                {
                    searchPos = panelContentStart + relPanelClose + PanelNameCloseLower.Length;
                    continue;
                }

                int bodyEndByte = panelOpenPos + bodyEndRel;
                int lineStartByte = chunkSpan[..panelOpenPos].LastIndexOf((byte)'\n');
                if (lineStartByte < 0) lineStartByte = 0;
                if (chunkSpan[lineStartByte..panelOpenPos].IndexOf("recv[LUATHREAD]"u8) >= 0)
                {
                    searchPos = bodyEndByte + BodyEndTagLower.Length;
                    continue;
                }

                ReadOnlySpan<byte> bodySpanBytes = chunkSpan[panelOpenPos..bodyEndByte];
                int valOpenRel = IndexOfAsciiIgnoreCase(bodySpanBytes, OtpDefectValueOpenLower);
                if (valOpenRel < 0)
                {
                    searchPos = bodyEndByte + BodyEndTagLower.Length;
                    continue;
                }

                int valContentStart = valOpenRel + OtpDefectValueOpenLower.Length;
                int valCloseRel = IndexOfAsciiIgnoreCase(bodySpanBytes[valContentStart..], "</otpdefectvalue>"u8);
                if (valCloseRel <= 0)
                {
                    searchPos = bodyEndByte + BodyEndTagLower.Length;
                    continue;
                }

                string panelName = Encoding.UTF8.GetString(chunkSpan.Slice(panelContentStart, relPanelClose)).Trim();
                string otpDefectVal = Encoding.UTF8.GetString(bodySpanBytes.Slice(valContentStart, valCloseRel)).Trim();

                if (!string.IsNullOrEmpty(panelName) && !string.IsNullOrEmpty(otpDefectVal) && otpDefectVal != "[]")
                {
                    if (results.Count == 0 ||
                        !string.Equals(results[^1].PanelName, panelName, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(results[^1].OtpDefectValue, otpDefectVal, StringComparison.Ordinal))
                    {
                        results.Add(new MesPanelDetailRecord
                        {
                            PanelName = panelName,
                            OtpDefectValue = otpDefectVal,
                            SourceFile = info.FullName
                        });
                    }
                }

                searchPos = bodyEndByte + BodyEndTagLower.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var arr = results.ToArray();
        _cache.Set(cacheKey, new CacheEntry(stamp, info.FullName, stamp, arr));
        return arr;
    }

    private const int WindowChunkBytes = 4 * 1024 * 1024;
    private const int WindowHeaderLookbackBytes = 8192;
    private const int MaxWindowScanRecords = 100_000;
    private const long MaxWindowScanBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan WindowPruneMargin = TimeSpan.FromHours(1);

    private static ReadOnlySpan<byte> OtpDefectValueCloseLower => "</otpdefectvalue>"u8;
    private static ReadOnlySpan<byte> TransactionStartTimeOpenLower => "<transactionstarttime>"u8;
    private static ReadOnlySpan<byte> TransactionStartTimeCloseLower => "</transactionstarttime>"u8;

    /// <summary>
    /// Streams MES panel records across rotated archives and the active log within [startTime, endTime].
    /// </summary>
    public IReadOnlyList<MesPanelDetailRecord> ExtractPanelRecordsInWindow(
        string mesLogPath,
        DateTime? startTime,
        DateTime? endTime,
        int maxRotatedFiles = 15,
        int maxBytes = 8 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mesLogPath))
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!startTime.HasValue && !endTime.HasValue)
        {
            return ExtractRecentPanelRecords(mesLogPath, maxBytes);
        }

        if (startTime.HasValue && endTime.HasValue && startTime.Value > endTime.Value)
        {
            return [];
        }

        var family = LogFileFamilyHelper.ResolveLogFileFamily(
            mesLogPath,
            maxRotatedFiles: Math.Max(0, maxRotatedFiles),
            includeFirstTimestamps: true);
        if (family is null || family.OldestToNewest.Count == 0)
        {
            return [];
        }

        var candidateEntries = SelectWindowEntries(family.OldestToNewest, startTime, endTime);
        if (candidateEntries.Count == 0)
        {
            return [];
        }

        var liveEntries = new List<(string Path, long Length)>(candidateEntries.Count);
        var stampBuilder = new StringBuilder();
        foreach (var entry in candidateEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fi = new FileInfo(entry.Path);
            if (!TryGetFileStamp(fi, out long len, out string entryStamp) || len <= 0)
            {
                continue;
            }

            liveEntries.Add((fi.FullName, len));
            stampBuilder.Append(fi.FullName).Append('|').Append(entryStamp).Append(';');
        }

        if (liveEntries.Count == 0)
        {
            return [];
        }

        string combinedStamp = stampBuilder.ToString();
        string cacheKey = $"WIN_SPC|{family.Identity.ActiveFilePath}|{startTime?.Ticks ?? -1}|{endTime?.Ticks ?? -1}|{maxRotatedFiles}";
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            cached is not null &&
            string.Equals(cached.PrimaryStamp, combinedStamp, StringComparison.Ordinal))
        {
            return cached.Results;
        }

        var results = new List<MesPanelDetailRecord>();
        long remainingScanBytes = MaxWindowScanBytes;

        foreach (var (filePath, fileLength) in liveEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remainingScanBytes <= 0 || results.Count >= MaxWindowScanRecords)
            {
                break;
            }

            ScanMesFileInWindow(
                filePath,
                fileLength,
                startTime,
                endTime,
                results,
                ref remainingScanBytes,
                cancellationToken);
        }

        var arr = results.ToArray();
        _cache.Set(cacheKey, new CacheEntry(combinedStamp, family.Identity.ActiveFilePath, combinedStamp, arr));
        return arr;
    }

    private static List<LogFileFamilyEntry> SelectWindowEntries(
        IReadOnlyList<LogFileFamilyEntry> oldestToNewest,
        DateTime? startTime,
        DateTime? endTime)
    {
        bool monotonicFirstTimestamps = HasMonotonicFirstTimestamps(oldestToNewest);
        var selected = new List<LogFileFamilyEntry>(oldestToNewest.Count);

        for (int i = 0; i < oldestToNewest.Count; i++)
        {
            var entry = oldestToNewest[i];
            if (!entry.Exists || entry.Length <= 0)
            {
                continue;
            }

            if (endTime.HasValue &&
                monotonicFirstTimestamps &&
                entry.FirstLogTimestamp.HasValue &&
                entry.FirstLogTimestamp.Value > endTime.Value + WindowPruneMargin)
            {
                continue;
            }

            if (startTime.HasValue)
            {
                if (entry.LastWriteTimeUtc.ToLocalTime() < startTime.Value - WindowPruneMargin)
                {
                    continue;
                }

                if (monotonicFirstTimestamps && HasSubsequentFileStartingBeforeCutoff(oldestToNewest, i, startTime.Value - WindowPruneMargin))
                {
                    continue;
                }
            }

            selected.Add(entry);
        }

        return selected;
    }

    private static bool HasMonotonicFirstTimestamps(IReadOnlyList<LogFileFamilyEntry> oldestToNewest)
    {
        DateTime? prev = null;
        for (int i = 0; i < oldestToNewest.Count; i++)
        {
            var ts = oldestToNewest[i].FirstLogTimestamp;
            if (!ts.HasValue)
            {
                continue;
            }

            if (prev.HasValue && ts.Value + WindowPruneMargin < prev.Value)
            {
                return false;
            }

            prev = ts.Value;
        }

        return true;
    }

    private static bool HasSubsequentFileStartingBeforeCutoff(
        IReadOnlyList<LogFileFamilyEntry> oldestToNewest,
        int currentIndex,
        DateTime cutoff)
    {
        for (int j = currentIndex + 1; j < oldestToNewest.Count; j++)
        {
            var nextTs = oldestToNewest[j].FirstLogTimestamp;
            if (nextTs.HasValue)
            {
                return nextTs.Value <= cutoff;
            }
        }

        return false;
    }

    private static void ScanMesFileInWindow(
        string filePath,
        long fileLength,
        DateTime? startTime,
        DateTime? endTime,
        List<MesPanelDetailRecord> results,
        ref long remainingScanBytes,
        CancellationToken cancellationToken)
    {
        int bufSize = (int)Math.Min(fileLength, WindowChunkBytes);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufSize);

        try
        {
            using var fs = FileIoRetryHelper.OpenSharedReadStreamWithRetry(
                filePath,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 65536,
                options: FileOptions.SequentialScan,
                cancellationToken: cancellationToken);

            long chunkStart = 0;
            while (chunkStart < fileLength && remainingScanBytes > 0 && results.Count < MaxWindowScanRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(bufSize, fileLength - chunkStart);
                fs.Seek(chunkStart, SeekOrigin.Begin);

                int got = 0;
                while (got < toRead)
                {
                    int n = fs.Read(buffer, got, toRead - got);
                    if (n <= 0)
                    {
                        break;
                    }
                    got += n;
                }

                if (got <= 0)
                {
                    break;
                }

                remainingScanBytes -= got;
                bool isAtEof = chunkStart + got >= fileLength;
                ReadOnlySpan<byte> chunkSpan = buffer.AsSpan(0, got);

                int searchPos = 0;
                int consumedEnd = 0;
                long nextChunkStart = chunkStart + got;
                bool stoppedForSplitBlock = false;

                while (searchPos < got)
                {
                    int relPanelOpen = IndexOfAsciiIgnoreCase(chunkSpan[searchPos..], PanelNameOpenLower);
                    if (relPanelOpen < 0)
                    {
                        break;
                    }

                    int panelOpenPos = searchPos + relPanelOpen;
                    int panelContentStart = panelOpenPos + PanelNameOpenLower.Length;
                    if (panelContentStart >= got)
                    {
                        stoppedForSplitBlock = TryDeferSplitBlock(chunkStart, panelOpenPos, consumedEnd, isAtEof, ref nextChunkStart);
                        break;
                    }

                    int bodyEndRel = IndexOfAsciiIgnoreCase(chunkSpan[panelContentStart..], BodyEndTagLower);
                    if (bodyEndRel < 0)
                    {
                        if (TryDeferSplitBlock(chunkStart, panelOpenPos, consumedEnd, isAtEof, ref nextChunkStart))
                        {
                            stoppedForSplitBlock = true;
                            break;
                        }

                        searchPos = panelContentStart;
                        continue;
                    }

                    int bodyEndByte = panelContentStart + bodyEndRel;
                    int afterBodyEnd = bodyEndByte + BodyEndTagLower.Length;

                    int panelSearchWin = Math.Min(bodyEndByte - panelContentStart, 256);
                    int relPanelClose = IndexOfAsciiIgnoreCase(chunkSpan.Slice(panelContentStart, panelSearchWin), PanelNameCloseLower);
                    if (relPanelClose < 0)
                    {
                        searchPos = panelContentStart;
                        continue;
                    }

                    if (bodyEndByte - panelOpenPos > 32768)
                    {
                        searchPos = panelContentStart + relPanelClose + PanelNameCloseLower.Length;
                        consumedEnd = Math.Max(consumedEnd, searchPos);
                        continue;
                    }

                    int lineStartByte = chunkSpan[..panelOpenPos].LastIndexOf((byte)'\n');
                    int lineStart = lineStartByte < 0 ? 0 : lineStartByte + 1;
                    if (chunkSpan[lineStart..panelOpenPos].IndexOf("recv[LUATHREAD]"u8) >= 0)
                    {
                        consumedEnd = afterBodyEnd;
                        searchPos = afterBodyEnd;
                        continue;
                    }

                    ReadOnlySpan<byte> bodySpan = chunkSpan[panelOpenPos..bodyEndByte];
                    int valOpenRel = IndexOfAsciiIgnoreCase(bodySpan, OtpDefectValueOpenLower);
                    if (valOpenRel < 0)
                    {
                        consumedEnd = afterBodyEnd;
                        searchPos = afterBodyEnd;
                        continue;
                    }

                    int valContentStart = valOpenRel + OtpDefectValueOpenLower.Length;
                    int valCloseRel = IndexOfAsciiIgnoreCase(bodySpan[valContentStart..], OtpDefectValueCloseLower);
                    if (valCloseRel <= 0)
                    {
                        consumedEnd = afterBodyEnd;
                        searchPos = afterBodyEnd;
                        continue;
                    }

                    ReadOnlySpan<byte> panelBytes = TrimAsciiWhitespace(chunkSpan.Slice(panelContentStart, relPanelClose));
                    ReadOnlySpan<byte> valBytes = TrimAsciiWhitespace(bodySpan.Slice(valContentStart, valCloseRel));
                    if (panelBytes.IsEmpty || valBytes.IsEmpty || valBytes.SequenceEqual("[]"u8))
                    {
                        consumedEnd = afterBodyEnd;
                        searchPos = afterBodyEnd;
                        continue;
                    }

                    int lookbackStart = Math.Max(0, panelOpenPos - WindowHeaderLookbackBytes);
                    ReadOnlySpan<byte> headerWindow = chunkSpan[lookbackStart..panelOpenPos];
                    int prevMsgEnd = IndexOfLastAsciiIgnoreCase(headerWindow, MessageEndTagLower);
                    if (prevMsgEnd >= 0)
                    {
                        headerWindow = headerWindow[(prevMsgEnd + MessageEndTagLower.Length)..];
                    }

                    int prevBodyEnd = IndexOfLastAsciiIgnoreCase(headerWindow, BodyEndTagLower);
                    if (prevBodyEnd >= 0)
                    {
                        headerWindow = headerWindow[(prevBodyEnd + BodyEndTagLower.Length)..];
                    }

                    if (HasProhibitedDtdBytes(headerWindow) || HasProhibitedDtdBytes(bodySpan))
                    {
                        consumedEnd = afterBodyEnd;
                        searchPos = afterBodyEnd;
                        continue;
                    }

                    if (!TryResolveBlockTimestampUtf8(headerWindow, out DateTime blockTime) ||
                        (startTime.HasValue && blockTime < startTime.Value) ||
                        (endTime.HasValue && blockTime > endTime.Value))
                    {
                        consumedEnd = afterBodyEnd;
                        searchPos = afterBodyEnd;
                        continue;
                    }

                    string panelName = Encoding.UTF8.GetString(panelBytes);
                    string otpDefectVal = Encoding.UTF8.GetString(valBytes);

                    if (results.Count == 0 ||
                        !string.Equals(results[^1].PanelName, panelName, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(results[^1].OtpDefectValue, otpDefectVal, StringComparison.Ordinal))
                    {
                        results.Add(new MesPanelDetailRecord
                        {
                            PanelName = panelName,
                            OtpDefectValue = otpDefectVal,
                            SourceFile = filePath
                        });

                        if (results.Count >= MaxWindowScanRecords)
                        {
                            break;
                        }
                    }

                    consumedEnd = afterBodyEnd;
                    searchPos = afterBodyEnd;
                }

                if (!stoppedForSplitBlock && !isAtEof)
                {
                    int tailOverlapStart = Math.Max(consumedEnd, got - WindowHeaderLookbackBytes);
                    if (tailOverlapStart > 0)
                    {
                        nextChunkStart = chunkStart + tailOverlapStart;
                    }
                }

                if (nextChunkStart <= chunkStart)
                {
                    nextChunkStart = chunkStart + Math.Max(1, got);
                }

                chunkStart = nextChunkStart;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Keep already-collected records from prior accessible files; skip locked/unreadable file.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryDeferSplitBlock(
        long chunkStart,
        int panelOpenPos,
        int consumedEnd,
        bool isAtEof,
        ref long nextChunkStart)
    {
        if (isAtEof)
        {
            return false;
        }

        int overlapStart = Math.Max(consumedEnd, panelOpenPos - WindowHeaderLookbackBytes);
        if (overlapStart <= 0)
        {
            return false;
        }

        nextChunkStart = chunkStart + overlapStart;
        return true;
    }

    private static bool HasProhibitedDtdBytes(ReadOnlySpan<byte> utf8Bytes) =>
        IndexOfAsciiIgnoreCase(utf8Bytes, "<!doctype"u8) >= 0 ||
        IndexOfAsciiIgnoreCase(utf8Bytes, "<!entity"u8) >= 0;

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        int start = 0;
        while (start < span.Length && span[start] <= (byte)' ')
        {
            start++;
        }

        int end = span.Length - 1;
        while (end >= start && span[end] <= (byte)' ')
        {
            end--;
        }

        return start <= end ? span.Slice(start, end - start + 1) : ReadOnlySpan<byte>.Empty;
    }

    private static int IndexOfLastAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> lowerAsciiNeedle)
    {
        if (lowerAsciiNeedle.IsEmpty || haystack.Length < lowerAsciiNeedle.Length)
        {
            return -1;
        }

        int lastIdx = -1;
        int offset = 0;
        while (offset <= haystack.Length - lowerAsciiNeedle.Length)
        {
            int rel = IndexOfAsciiIgnoreCase(haystack[offset..], lowerAsciiNeedle);
            if (rel < 0)
            {
                break;
            }

            lastIdx = offset + rel;
            offset = lastIdx + 1;
        }

        return lastIdx;
    }

    private static bool TryResolveBlockTimestampUtf8(ReadOnlySpan<byte> headerWindow, out DateTime timestamp)
    {
        if (TryFindLastBracketTimestampUtf8(headerWindow, out timestamp))
        {
            return true;
        }

        int txOpenRel = IndexOfLastAsciiIgnoreCase(headerWindow, TransactionStartTimeOpenLower);
        if (txOpenRel >= 0)
        {
            int contentStart = txOpenRel + TransactionStartTimeOpenLower.Length;
            int txCloseRel = IndexOfAsciiIgnoreCase(headerWindow[contentStart..], TransactionStartTimeCloseLower);
            if (txCloseRel > 0)
            {
                ReadOnlySpan<byte> txSpan = TrimAsciiWhitespace(headerWindow.Slice(contentStart, txCloseRel));
                if (TryParseFlexibleTimestampUtf8(txSpan, out timestamp))
                {
                    return true;
                }
            }
        }

        timestamp = default;
        return false;
    }

    private static bool TryFindLastBracketTimestampUtf8(ReadOnlySpan<byte> span, out DateTime timestamp)
    {
        int pos = span.Length - 1;
        while (pos >= 20)
        {
            int closeIdx = span[..(pos + 1)].LastIndexOf((byte)']');
            if (closeIdx < 20)
            {
                break;
            }

            int searchOpenFrom = Math.Max(0, closeIdx - 32);
            int relOpen = span[searchOpenFrom..closeIdx].LastIndexOf((byte)'[');
            if (relOpen >= 0)
            {
                int openIdx = searchOpenFrom + relOpen;
                ReadOnlySpan<byte> inner = span.Slice(openIdx + 1, closeIdx - openIdx - 1);
                if (TryParseDelimitedTimestampUtf8(inner, out timestamp))
                {
                    return true;
                }
            }

            pos = closeIdx - 1;
        }

        timestamp = default;
        return false;
    }

    private static bool TryParseFlexibleTimestampUtf8(ReadOnlySpan<byte> span, out DateTime timestamp)
    {
        if (TryParseDelimitedTimestampUtf8(span, out timestamp))
        {
            return true;
        }

        // Compact MES format: yyyyMMddHHmmss or yyyyMMddHHmmssfff
        if (span.Length is 14 or 17 &&
            TryParseFixedDigits(span.Slice(0, 4), out int year) &&
            TryParseFixedDigits(span.Slice(4, 2), out int month) &&
            TryParseFixedDigits(span.Slice(6, 2), out int day) &&
            TryParseFixedDigits(span.Slice(8, 2), out int hour) &&
            TryParseFixedDigits(span.Slice(10, 2), out int minute) &&
            TryParseFixedDigits(span.Slice(12, 2), out int second))
        {
            int ms = 0;
            if (span.Length == 17 && !TryParseFixedDigits(span.Slice(14, 3), out ms))
            {
                timestamp = default;
                return false;
            }

            return TryBuildLocalDateTime(year, month, day, hour, minute, second, ms, out timestamp);
        }

        timestamp = default;
        return false;
    }

    private static bool TryParseDelimitedTimestampUtf8(ReadOnlySpan<byte> span, out DateTime timestamp)
    {
        if (span.Length < 19 ||
            (span[4] != (byte)'-' && span[4] != (byte)'/') ||
            span[7] != span[4] ||
            (span[10] != (byte)' ' && span[10] != (byte)'T' && span[10] != (byte)'t') ||
            span[13] != (byte)':' ||
            span[16] != (byte)':')
        {
            timestamp = default;
            return false;
        }

        if (!TryParseFixedDigits(span.Slice(0, 4), out int year) ||
            !TryParseFixedDigits(span.Slice(5, 2), out int month) ||
            !TryParseFixedDigits(span.Slice(8, 2), out int day) ||
            !TryParseFixedDigits(span.Slice(11, 2), out int hour) ||
            !TryParseFixedDigits(span.Slice(14, 2), out int minute) ||
            !TryParseFixedDigits(span.Slice(17, 2), out int second))
        {
            timestamp = default;
            return false;
        }

        int ms = 0;
        if (span.Length > 19)
        {
            if (span[19] != (byte)'.' || span.Length == 20 || span.Length > 27)
            {
                timestamp = default;
                return false;
            }

            ReadOnlySpan<byte> frac = span[20..];
            int weight = 100;
            for (int i = 0; i < frac.Length; i++)
            {
                byte b = frac[i];
                if ((uint)(b - '0') > 9)
                {
                    timestamp = default;
                    return false;
                }

                if (weight > 0)
                {
                    ms += (b - '0') * weight;
                    weight /= 10;
                }
            }
        }

        return TryBuildLocalDateTime(year, month, day, hour, minute, second, ms, out timestamp);
    }

    private static bool TryParseFixedDigits(ReadOnlySpan<byte> digits, out int value)
    {
        int acc = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            uint d = (uint)(digits[i] - '0');
            if (d > 9)
            {
                value = 0;
                return false;
            }

            acc = (acc * 10) + (int)d;
        }

        value = acc;
        return true;
    }

    private static bool TryBuildLocalDateTime(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second,
        int millisecond,
        out DateTime timestamp)
    {
        if (year is < 2000 or > 2099 ||
            month is < 1 or > 12 ||
            hour is < 0 or > 23 ||
            minute is < 0 or > 59 ||
            second is < 0 or > 59 ||
            millisecond is < 0 or > 999 ||
            day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            timestamp = default;
            return false;
        }

        timestamp = new DateTime(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);
        return true;
    }

    private static ReadOnlySpan<byte> PanelNameOpenLower => "<panelname>"u8;
    private static ReadOnlySpan<byte> PanelNameCloseLower => "</panelname>"u8;
    private static ReadOnlySpan<byte> BodyEndTagLower => "</body>"u8;
    private static ReadOnlySpan<byte> MessageEndTagLower => "</message>"u8;
    private static ReadOnlySpan<byte> OtpDefectCodeOpenLower => "<otpdefectcode>"u8;
    private static ReadOnlySpan<byte> OtpDefectValueOpenLower => "<otpdefectvalue>"u8;

    private static bool TryFindNextPanelMatch(
        ReadOnlySpan<byte> chunkSpan,
        int searchPos,
        byte[]? lowerAsciiNeedle,
        string targetPanel,
        out int matchBytePos,
        out int nextSearchPos)
    {
        if (lowerAsciiNeedle is not null)
        {
            int relIdx = IndexOfAsciiIgnoreCase(chunkSpan[searchPos..], lowerAsciiNeedle);
            if (relIdx < 0)
            {
                matchBytePos = 0;
                nextSearchPos = searchPos;
                return false;
            }

            matchBytePos = searchPos + relIdx;
            nextSearchPos = matchBytePos + lowerAsciiNeedle.Length;
            return true;
        }

        // Byte-level scan on raw UTF-8 buffer for non-ASCII panel names without decoding the entire 4 MB chunk onto LOH.
        int maxPanelByteWindow = Math.Max(64, Encoding.UTF8.GetMaxByteCount(targetPanel.Length) + 32);
        int pos = searchPos;
        while (pos < chunkSpan.Length)
        {
            int relOpen = IndexOfAsciiIgnoreCase(chunkSpan[pos..], PanelNameOpenLower);
            if (relOpen < 0)
                break;

            int openPos = pos + relOpen;
            int contentStart = openPos + PanelNameOpenLower.Length;
            if (contentStart >= chunkSpan.Length)
                break;

            int searchWindowLen = Math.Min(chunkSpan.Length - contentStart, maxPanelByteWindow);
            int relClose = IndexOfAsciiIgnoreCase(chunkSpan.Slice(contentStart, searchWindowLen), PanelNameCloseLower);
            if (relClose < 0)
            {
                pos = contentStart;
                continue;
            }

            if (MatchesUtf8IgnoreCase(chunkSpan.Slice(contentStart, relClose), targetPanel))
            {
                matchBytePos = openPos;
                nextSearchPos = contentStart + relClose + PanelNameCloseLower.Length;
                return true;
            }

            pos = contentStart + relClose + PanelNameCloseLower.Length;
        }

        matchBytePos = 0;
        nextSearchPos = pos;
        return false;
    }

    private static bool MatchesUtf8IgnoreCase(ReadOnlySpan<byte> utf8Bytes, string expected)
    {
        int maxChars = Encoding.UTF8.GetMaxCharCount(utf8Bytes.Length);
        char[]? rented = maxChars > 256 ? ArrayPool<char>.Shared.Rent(maxChars) : null;
        try
        {
            Span<char> charBuf = rented is not null ? rented.AsSpan(0, maxChars) : stackalloc char[256];
            int written = Encoding.UTF8.GetChars(utf8Bytes, charBuf);
            return charBuf[..written].Equals(expected.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    private static bool HasProhibitedDtd(ReadOnlySpan<char> text) =>
        text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase);

    private static ReadOnlySpan<char> SliceCurrentMessageHeader(string headerChunk)
    {
        ReadOnlySpan<char> span = headerChunk.AsSpan();
        int prevEnd = span.LastIndexOf("</MESSAGE>", StringComparison.OrdinalIgnoreCase);
        return prevEnd >= 0 ? span[(prevEnd + "</MESSAGE>".Length)..] : span;
    }

    private static RawMesHit? TryExtractRawMesHit(string bodyChunk, string? returnChunk, string headerChunk)
    {
        if (HasProhibitedDtd(SliceCurrentMessageHeader(headerChunk)) ||
            HasProhibitedDtd(bodyChunk) ||
            (returnChunk is not null && HasProhibitedDtd(returnChunk)))
        {
            return null;
        }

        try
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
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> lowerAsciiNeedle)
    {
        int needleLen = lowerAsciiNeedle.Length;
        if (needleLen == 0 || haystack.Length < needleLen)
            return -1;

        byte firstLower = lowerAsciiNeedle[0];
        bool isFirstLetter = (uint)(firstLower - 'a') <= ('z' - 'a');
        byte firstUpper = isFirstLetter ? (byte)(firstLower - 32) : firstLower;

        if (needleLen == 1)
        {
            return isFirstLetter
                ? haystack.IndexOfAny(firstLower, firstUpper)
                : haystack.IndexOf(firstLower);
        }

        int lastOffset = needleLen - 1;
        byte lastLower = lowerAsciiNeedle[lastOffset];
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

            // Fast O(1) last-byte precheck skips >90% of '<...>' non-matching XML tags before scanning inside
            byte lastByte = haystack[pos + lastOffset];
            if ((uint)(lastByte - 'A') <= ('Z' - 'A'))
            {
                lastByte = (byte)(lastByte + 32);
            }
            if (lastByte != lastLower)
            {
                searchOffset = pos + 1;
                continue;
            }

            bool matched = true;
            for (int j = 1; j < lastOffset; j++)
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
