using System.Buffers;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Services;

/// <summary>
/// Resolved image file paths inside a single camera channel's inspection directory
/// (<c>[Result]_[Serial]_[Timestamp]_[CamStation]</c>).
/// </summary>
public sealed record CameraChannelImageSet
{
    public required string SerialNumber { get; init; }
    public required string CameraChannel { get; init; }
    public string Side { get; init; } = "Left";
    public string RootPath { get; init; } = string.Empty;
    public bool Offline { get; init; }
    public string? PanelDirectoryPath { get; init; }
    public bool IsNgDirectory { get; init; }

    /// <summary>
    /// Overview / algorithm integrated image (<c>piresult/AlgIntegrate.jpg</c> or <c>piresult/Main*_L255.jpg</c>).
    /// </summary>
    public string? OverviewImagePath { get; init; }

    /// <summary>
    /// Cropped defect region images inside <c>piresult/defectPicture/</c> or <c>piresult/classifyPicture/</c>.
    /// </summary>
    public IReadOnlyList<string> DefectCropPaths { get; init; } = [];

    /// <summary>
    /// Best matching defect crop image (prioritizing filenames matching the active defect code).
    /// </summary>
    public string? PrimaryDefectCropPath => DefectCropPaths.Count > 0 ? DefectCropPaths[0] : null;

    /// <summary>
    /// Ultra-high-resolution raw image (<c>photo/Main*.png</c>, ~50MB 17701x14208).
    /// Resolved by path only — never loaded into RAM unless the user explicitly triggers 1:1 inspection.
    /// </summary>
    public string? RawHighResPngPath { get; init; }

    /// <summary>
    /// Preferred image for Wallboard Live NG Unit Spotlight:
    /// prioritizes <see cref="PrimaryDefectCropPath"/>, falling back to <see cref="OverviewImagePath"/>.
    /// </summary>
    public string? PreferredSpotlightPath => PrimaryDefectCropPath ?? OverviewImagePath;

    /// <summary>
    /// Preferred initial image for View Detail main viewport:
    /// loads the overview JPG (<c>piresult/AlgIntegrate.jpg</c> or <c>Main*_L255.jpg</c>) first, falling back to defect crop.
    /// </summary>
    public string? PreferredPreviewPath => OverviewImagePath ?? PrimaryDefectCropPath;

    public bool HasDefectCrop => PrimaryDefectCropPath is not null;

    public bool HasRawHighResPng => RawHighResPngPath is not null;

    public bool HasAnyImage => PreferredSpotlightPath is not null || RawHighResPngPath is not null;
}

/// <summary>
/// Paired Left (A1/B1) and Right (A2/B2) camera inspection image sets for Side-by-Side comparison and Wallboard Spotlight.
/// </summary>
public sealed record DualCameraImagePair(
    string SerialNumber,
    CameraChannelImageSet Left,
    CameraChannelImageSet Right)
{
    /// <summary>
    /// Returns the primary camera channel that captured the defect (prioritizing defect crops, then NG folder prefix).
    /// </summary>
    public CameraChannelImageSet? PrimaryDefectChannel =>
        Left.HasDefectCrop ? Left :
        Right.HasDefectCrop ? Right :
        Left.IsNgDirectory && Left.HasAnyImage ? Left :
        Right.IsNgDirectory && Right.HasAnyImage ? Right :
        Left.HasAnyImage ? Left :
        Right.HasAnyImage ? Right : null;

    /// <summary>
    /// Summary badge of camera stations that captured images/defects (e.g., <c>"B1"</c> or <c>"B1 / B2"</c>).
    /// </summary>
    public string DetectedCameraSummary
    {
        get
        {
            bool leftNg = Left.HasDefectCrop || (Left.IsNgDirectory && Left.HasAnyImage);
            bool rightNg = Right.HasDefectCrop || (Right.IsNgDirectory && Right.HasAnyImage);

            if (leftNg && rightNg) return $"{Left.CameraChannel} / {Right.CameraChannel}";
            if (leftNg) return Left.CameraChannel;
            if (rightNg) return Right.CameraChannel;

            if (Left.HasAnyImage && Right.HasAnyImage) return $"{Left.CameraChannel} / {Right.CameraChannel}";
            if (Left.HasAnyImage) return Left.CameraChannel;
            if (Right.HasAnyImage) return Right.CameraChannel;
            return string.Empty;
        }
    }
}

/// <summary>
/// Resolves AOI camera directory hierarchies (<c>[Result]_[Serial]_[Timestamp]_[CamStation]</c>)
/// and locates overview images (<c>piresult/AlgIntegrate.jpg</c>, <c>Main*_L255.jpg</c>),
/// defect crops (<c>piresult/defectPicture/</c>, <c>piresult/classifyPicture/</c>),
/// and on-demand 1:1 raw PNGs (<c>photo/Main*.png</c>).
/// </summary>
public sealed class ImageLocatorService
{
    private static readonly SearchValues<char> DefectTokenDelimiters = SearchValues.Create("|,; ");

    private static readonly string[] OverviewCandidateNames =
    [
        "AlgIntegrate.jpg",
        "AlgIntegrate.jpeg",
        "AlgIntegrate.bmp",
        "AlgIntegrate.png"
    ];

    private static readonly string[] DefectSubfolderNames =
    [
        "defectPicture",
        "classifyPicture"
    ];

    private readonly ICameraImageService _cameraImageService;

    public ImageLocatorService(ICameraImageService? cameraImageService = null)
    {
        _cameraImageService = cameraImageService ?? new CameraImageService();
    }

    /// <summary>
    /// Fast-matches the panel folder for <paramref name="serialNumber"/> and <paramref name="cameraChannel"/>
    /// and resolves overview, defect crop, and raw 1:1 PNG paths.
    /// </summary>
    public async Task<CameraChannelImageSet> LocateChannelImagesAsync(
        string rootPath,
        string serialNumber,
        string cameraChannel,
        string side = "Left",
        DateTime? timestamp = null,
        string? defectCode = null,
        bool skipHostCheck = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(serialNumber))
        {
            return new CameraChannelImageSet
            {
                SerialNumber = serialNumber ?? string.Empty,
                CameraChannel = cameraChannel ?? string.Empty,
                Side = side,
                RootPath = rootPath ?? string.Empty
            };
        }

        if (!skipHostCheck)
        {
            bool reachable = await _cameraImageService
                .TestHostReachableAsync(rootPath, timeoutMs: 1200, cancellationToken)
                .ConfigureAwait(false);
            if (!reachable)
            {
                return new CameraChannelImageSet
                {
                    SerialNumber = serialNumber,
                    CameraChannel = cameraChannel,
                    Side = side,
                    RootPath = rootPath,
                    Offline = true
                };
            }
        }

        string? panelDir = await _cameraImageService
            .FindPanelImageFolderAsync(
                rootPath,
                serialNumber,
                cameraChannel,
                timestamp,
                skipHostCheck: true,
                cancellationToken)
            .ConfigureAwait(false);

        return await Task.Run(
            () => ResolveChannelImagesFromDirectory(panelDir, serialNumber, cameraChannel, side, rootPath, defectCode),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves Left and Right camera channels in parallel for Side-by-Side comparison and Wallboard Spotlight.
    /// </summary>
    public async Task<DualCameraImagePair> LocateDualCameraPairAsync(
        string leftRootPath,
        string leftCameraChannel,
        string? rightRootPath,
        string? rightCameraChannel,
        string serialNumber,
        DateTime? timestamp = null,
        string? defectCode = null,
        CancellationToken cancellationToken = default)
    {
        var leftTask = LocateChannelImagesAsync(
            leftRootPath,
            serialNumber,
            leftCameraChannel,
            side: "Left",
            timestamp: timestamp,
            defectCode: defectCode,
            cancellationToken: cancellationToken);

        Task<CameraChannelImageSet> rightTask = !string.IsNullOrWhiteSpace(rightRootPath) && !string.IsNullOrWhiteSpace(rightCameraChannel)
            ? LocateChannelImagesAsync(
                rightRootPath,
                serialNumber,
                rightCameraChannel,
                side: "Right",
                timestamp: timestamp,
                defectCode: defectCode,
                cancellationToken: cancellationToken)
            : Task.FromResult(new CameraChannelImageSet
            {
                SerialNumber = serialNumber,
                CameraChannel = rightCameraChannel ?? "Cam 2",
                Side = "Right"
            });

        await Task.WhenAll(leftTask, rightTask).ConfigureAwait(false);
        return new DualCameraImagePair(serialNumber, await leftTask.ConfigureAwait(false), await rightTask.ConfigureAwait(false));
    }

    /// <summary>
    /// Inspects an already-located panel folder (<c>[Result]_[Serial]_[Timestamp]_[CamStation]</c>)
    /// to resolve <c>piresult/AlgIntegrate.jpg</c>, <c>piresult/Main*_L255.jpg</c>,
    /// <c>piresult/defectPicture/*</c>, and <c>photo/Main*.png</c>.
    /// </summary>
    public static CameraChannelImageSet ResolveChannelImagesFromDirectory(
        string? panelDirectoryPath,
        string serialNumber,
        string cameraChannel,
        string side = "Left",
        string rootPath = "",
        string? defectCode = null)
    {
        if (string.IsNullOrWhiteSpace(panelDirectoryPath) || !Directory.Exists(panelDirectoryPath))
        {
            return new CameraChannelImageSet
            {
                SerialNumber = serialNumber ?? string.Empty,
                CameraChannel = cameraChannel ?? string.Empty,
                Side = side,
                RootPath = rootPath
            };
        }

        string folderLeaf = Path.GetFileName(panelDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        bool isNgDir = folderLeaf.StartsWith("NG_", StringComparison.OrdinalIgnoreCase);

        string? overviewPath = null;
        var defectCrops = new List<string>();
        string? rawPngPath = null;

        try
        {
            // 1. Inspect piresult/ directory
            string piResultDir = Path.Combine(panelDirectoryPath, "piresult");
            if (Directory.Exists(piResultDir))
            {
                overviewPath = FindOverviewInPiResult(piResultDir);
                CollectDefectCrops(piResultDir, defectCode, defectCrops);
            }

            // 2. Inspect photo/ directory for 1:1 ultra-high-res Main*.png (path only, never auto-loaded)
            string photoDir = Path.Combine(panelDirectoryPath, "photo");
            if (Directory.Exists(photoDir))
            {
                rawPngPath = FindRawHighResPng(photoDir);
            }

            // 3. Fallback: if flat folder structure (or overview not in piresult/), check top-level files
            if (overviewPath is null)
            {
                overviewPath = FindTopLevelOverviewFallback(panelDirectoryPath);
            }
        }
        catch
        {
            // Swallow transient SMB enumeration errors; caller handles empty image set gracefully
        }

        return new CameraChannelImageSet
        {
            SerialNumber = serialNumber ?? string.Empty,
            CameraChannel = cameraChannel ?? string.Empty,
            Side = side,
            RootPath = rootPath,
            Offline = false,
            PanelDirectoryPath = panelDirectoryPath,
            IsNgDirectory = isNgDir,
            OverviewImagePath = overviewPath,
            DefectCropPaths = defectCrops,
            RawHighResPngPath = rawPngPath
        };
    }

    private static string? FindOverviewInPiResult(string piResultDir)
    {
        // Priority 1: piresult/AlgIntegrate.jpg
        foreach (var candidate in OverviewCandidateNames)
        {
            string fullPath = Path.Combine(piResultDir, candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        // Priority 2: piresult/Main*_L255.jpg (or any Main*_L255.*)
        try
        {
            string? matchedL255 = FindSmallestFileByName(piResultDir, "Main*_L255*", requireSupportedExtension: true);
            if (matchedL255 is not null)
            {
                return matchedL255;
            }

            // Priority 3: Any supported overview image directly inside piresult/
            return Directory.EnumerateFiles(piResultDir)
                .Where(f => IsSupportedImageExtension(Path.GetExtension(f.AsSpan())))
                .OrderByDescending(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static void CollectDefectCrops(string piResultDir, string? defectCode, List<string> targetList)
    {
        var rawCandidates = new List<string>();

        foreach (var subName in DefectSubfolderNames)
        {
            string subDir = Path.Combine(piResultDir, subName);
            if (!Directory.Exists(subDir)) continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(subDir, "*", SearchOption.AllDirectories))
                {
                    if (IsSupportedImageExtension(Path.GetExtension(file.AsSpan())))
                    {
                        rawCandidates.Add(file);
                    }
                }
            }
            catch
            {
            }
        }

        if (rawCandidates.Count == 0) return;

        // Extract primary defect token (e.g. "XYLV" from "XYLV | POWERON")
        string? primaryToken = ExtractPrimaryDefectToken(defectCode);

        var ordered = rawCandidates
            .OrderByDescending(f =>
                primaryToken is not null &&
                Path.GetFileNameWithoutExtension(f.AsSpan()).Contains(primaryToken.AsSpan(), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.Contains("defectPicture", StringComparison.OrdinalIgnoreCase))
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        targetList.AddRange(ordered);
    }

    private static string? FindRawHighResPng(string photoDir)
    {
        try
        {
            string? firstMainPng = FindSmallestFileByName(photoDir, "Main*.png", requireSupportedExtension: false);
            if (firstMainPng is not null)
            {
                return firstMainPng;
            }

            return Directory.EnumerateFiles(photoDir)
                .Where(f => IsSupportedImageExtension(Path.GetExtension(f.AsSpan())))
                .OrderByDescending(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindSmallestFileByName(string directory, string searchPattern, bool requireSupportedExtension)
    {
        string? bestPath = null;
        ReadOnlySpan<char> bestName = default;

        foreach (string file in Directory.EnumerateFiles(directory, searchPattern))
        {
            if (requireSupportedExtension && !IsSupportedImageExtension(Path.GetExtension(file.AsSpan())))
            {
                continue;
            }

            ReadOnlySpan<char> fileName = Path.GetFileName(file.AsSpan());
            if (bestPath is null || fileName.CompareTo(bestName, StringComparison.OrdinalIgnoreCase) < 0)
            {
                bestPath = file;
                bestName = fileName;
            }
        }

        return bestPath;
    }

    private static string? ExtractPrimaryDefectToken(string? defectCode)
    {
        if (string.IsNullOrWhiteSpace(defectCode) || string.Equals(defectCode, "-", StringComparison.Ordinal))
        {
            return null;
        }

        ReadOnlySpan<char> remaining = defectCode.AsSpan();
        while (!remaining.IsEmpty)
        {
            int delimIdx = remaining.IndexOfAny(DefectTokenDelimiters);
            ReadOnlySpan<char> segment = delimIdx >= 0 ? remaining[..delimIdx] : remaining;
            segment = segment.Trim();
            if (!segment.IsEmpty)
            {
                return segment.ToString();
            }

            if (delimIdx < 0)
            {
                break;
            }

            remaining = remaining[(delimIdx + 1)..];
        }

        return null;
    }

    private static bool IsSupportedImageExtension(ReadOnlySpan<char> ext)
    {
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindTopLevelOverviewFallback(string panelDirectoryPath)
    {
        try
        {
            foreach (var candidate in OverviewCandidateNames)
            {
                string fullPath = Path.Combine(panelDirectoryPath, candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }

            return Directory.EnumerateFiles(panelDirectoryPath)
                .Where(f => IsSupportedImageExtension(Path.GetExtension(f.AsSpan())))
                .OrderByDescending(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}

