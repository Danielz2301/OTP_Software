using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace OTPSoftware.Core.Helpers;

/// <summary>
/// Span-based UNC parsing and path/token validation helpers to prevent NTLM leaks,
/// directory traversal, and wildcard injection across SMB/local file operations.
/// </summary>
public static class SecurityPathValidator
{
    private static readonly SearchValues<char> ForbiddenTokenChars = SearchValues.Create(
        [.. Path.GetInvalidFileNameChars(), '*', '?', '/', '\\', ':', '<', '>', '|', '"']);

    private static readonly SearchValues<char> UnsafePathChars = SearchValues.Create("\"\0\r\n");

    private static bool IsDirectorySeparator(char ch) => ch is '\\' or '/';

    /// <summary>
    /// Extracts the server host or IP from UNC-like paths (<c>\\server\share</c>, <c>//server/share</c>,
    /// mixed slashes, <c>\\?\UNC\server\share</c>, <c>\\.\UNC\server\share</c>).
    /// Returns <see cref="string.Empty"/> for local, device (<c>\\?\C:\</c>), or malformed paths.
    /// </summary>
    public static string ExtractUncServerName(string? uncPath)
    {
        if (string.IsNullOrWhiteSpace(uncPath))
            return string.Empty;

        ReadOnlySpan<char> span = uncPath.AsSpan().TrimStart();
        if (span.Length < 3 || !IsDirectorySeparator(span[0]) || !IsDirectorySeparator(span[1]))
            return string.Empty;

        span = span[2..];
        if (span.IsEmpty)
            return string.Empty;

        if ((span[0] is '?' or '.') && span.Length >= 2 && IsDirectorySeparator(span[1]))
        {
            ReadOnlySpan<char> afterPrefix = span[2..];
            if (afterPrefix.Length >= 4 &&
                afterPrefix.StartsWith("UNC", StringComparison.OrdinalIgnoreCase) &&
                IsDirectorySeparator(afterPrefix[3]))
            {
                span = afterPrefix[4..];
            }
            else
            {
                return string.Empty;
            }
        }
        else
        {
            while (!span.IsEmpty && IsDirectorySeparator(span[0]))
            {
                span = span[1..];
            }
        }

        if (span.IsEmpty)
            return string.Empty;

        int sepIdx = span.IndexOfAny('\\', '/');
        ReadOnlySpan<char> host = sepIdx >= 0 ? span[..sepIdx] : span;
        host = host.Trim();
        return host.IsEmpty ? string.Empty : host.ToString();
    }

    /// <summary>
    /// Returns <see langword="true"/> for any path beginning with two directory separators (<c>\\</c>, <c>//</c>, etc.)
    /// unless it is explicitly a local DOS drive device path (<c>\\?\C:\</c> or <c>\\.\C:\</c>).
    /// Used to ensure malformed UNC prefixes never fall through to <c>Directory.Exists</c>/<c>File.Exists</c> and trigger SMB/NTLM auth.
    /// </summary>
    public static bool IsUncLikePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        ReadOnlySpan<char> span = path.AsSpan().TrimStart();
        if (span.Length < 2 || !IsDirectorySeparator(span[0]) || !IsDirectorySeparator(span[1]))
            return false;

        if (span.Length >= 6 &&
            span[2] is '?' or '.' &&
            IsDirectorySeparator(span[3]) &&
            char.IsAsciiLetter(span[4]) &&
            span[5] == ':')
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates single-segment identifiers used in file names or directory search patterns
    /// (e.g., panel serials, camera codes, shift keys) against traversal, separators, and wildcards.
    /// </summary>
    public static bool IsSafeIdentifierToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        ReadOnlySpan<char> span = token.AsSpan().Trim();
        if (span.IsEmpty)
            return false;

        if (span.Equals(".", StringComparison.Ordinal) ||
            span.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (span.ContainsAny(ForbiddenTokenChars))
            return false;

        foreach (char ch in span)
        {
            if (char.IsControl(ch))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Ensures a composed candidate path remains strictly inside <paramref name="rootDir"/> after full canonicalization.
    /// </summary>
    public static bool IsPathWithinRoot(string rootDir, string candidatePath)
    {
        string fullRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullCandidate = Path.GetFullPath(candidatePath);

        if (fullCandidate.Length <= fullRoot.Length)
            return false;

        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
               IsDirectorySeparator(fullCandidate[fullRoot.Length]);
    }

    /// <summary>
    /// Validates that <paramref name="path"/> is a safe local path or a trusted factory UNC endpoint
    /// (loopback, RFC1918 private IPv4, or explicitly present in <paramref name="allowedRoots"/>)
    /// without control characters or Windows device namespace prefixes.
    /// </summary>
    public static bool IsTrustedFactoryPath(string? path, IEnumerable<string>? allowedRoots = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string trimmed = path.Trim();
        if (trimmed.IndexOfAny(UnsafePathChars) >= 0)
            return false;

        if (IsDeviceNamespacePrefix(trimmed))
            return false;

        if (!IsUncLikePath(trimmed))
            return Path.IsPathFullyQualified(trimmed);

        string host = ExtractUncServerName(trimmed);
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (IsLoopbackOrPrivateLanHost(host))
            return true;

        if (allowedRoots is not null)
        {
            foreach (string root in allowedRoots)
            {
                if (string.IsNullOrWhiteSpace(root) || !IsUncLikePath(root))
                    continue;

                string allowedHost = ExtractUncServerName(root);
                if (!string.IsNullOrWhiteSpace(allowedHost) &&
                    string.Equals(host, allowedHost, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Validates that <paramref name="candidatePath"/> resolves strictly within or equal to <paramref name="root"/>.
    /// </summary>
    public static bool IsPathWithinOrEqualRoot(string? root, string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidatePath))
            return false;

        try
        {
            string fullRoot = Path.GetFullPath(root.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullCandidate = Path.GetFullPath(candidatePath.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            string rootWithSep = fullRoot + Path.DirectorySeparatorChar;
            return fullCandidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsDeviceNamespacePrefix(string trimmedPath) =>
        trimmedPath.StartsWith(@"\\.\", StringComparison.Ordinal) ||
        trimmedPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
        trimmedPath.StartsWith(@"//./", StringComparison.Ordinal) ||
        trimmedPath.StartsWith(@"//?/", StringComparison.Ordinal);

    private static bool IsLoopbackOrPrivateLanHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host, out var ip))
            return false;

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        byte[] b = ip.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168);
    }
}
