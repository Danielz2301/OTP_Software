---
name: desktop-appsec-and-hardened-io
description: Use this skill when reviewing, implementing, or hardening Windows desktop security in OTPSoftware, including UNC path NTLM leak prevention, safe process spawning (Process.Start), image decompression bomb mitigation, and XML XXE protection.
---

# Windows Desktop Application Security & Hardened I/O

This skill establishes defensive security standards and implementation patterns to harden `OTPSoftware.App` and `OTPSoftware.Core` against desktop-specific vulnerabilities in industrial factory networks.

---

## 1. Threat Model for Factory Floor Desktop Systems

Industrial inspection systems interact continuously with external network endpoints (`\\192.168.3.x`), parse remote log files, decode multi-gigapixel camera frames, and launch local Windows processes. 

Key threats addressed by this skill:
- **NetNTLM Hash Theft**: Rogue UNC paths triggering automatic Windows authentication handshakes.
- **Arbitrary Command Injection**: Malicious folder names or panel tokens executing commands via `Process.Start`.
- **Image Decompression Bombs**: Forged image dimensions causing unhandled native WIC crashes or memory exhaustion.
- **XML External Entity (XXE) Injection**: Untrusted XML payloads in `MES_1.log` exfiltrating local files or triggering Billion Laughs DoS.

---

## 2. UNC Path Hardening & NetNTLM Theft Defense

In Windows, querying an untrusted UNC path (`\\rogue-server\share\file`) causes the OS to automatically initiate SMB authentication, leaking the logged-in Windows user's NetNTLM hash.

### Defensive Validation Rule
All UNC paths in configuration (`settings.json`) or constructed dynamically from machine definitions must be strictly validated against trusted private IP ranges (RFC 1918) before any I/O call:

```csharp
public static class SecurityPathValidator
{
    public static bool IsTrustedFactoryUncPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        // Extract host or IP from UNC path: \\<host>\<share>\...
        ReadOnlySpan<char> span = path.AsSpan(2);
        int slashIdx = span.IndexOf('\\');
        if (slashIdx <= 0) return false;

        string hostOrIp = span[..slashIdx].ToString();

        // Must be a valid IP address within private factory network
        if (IPAddress.TryParse(hostOrIp, out var ip))
        {
            return IsPrivateSubnet(ip);
        }

        // Hostnames must not contain dots (prevents external FQDN resolution e.g. \\attacker.com)
        return !hostOrIp.Contains('.');
    }

    private static bool IsPrivateSubnet(IPAddress ip)
    {
        byte[] bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false; // IPv4 only

        // Check 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
```

---

## 3. Safe Process Spawning (`Process.Start` Hardening)

When opening camera folders in Windows Explorer or launching external inspection utilities, **never** use unescaped string concatenation with `UseShellExecute = true`.

### Secure Explorer Launch Pattern:
```csharp
public static void OpenFolderSafelyInExplorer(string directoryPath)
{
    if (string.IsNullOrWhiteSpace(directoryPath)) return;

    // 1. Verify directory exists and is a trusted path
    if (!Directory.Exists(directoryPath) || !SecurityPathValidator.IsTrustedFactoryUncPath(directoryPath))
    {
        return;
    }

    // 2. Launch explorer without invoking cmd.exe or the Windows Shell interpreter
    var psi = new ProcessStartInfo
    {
        FileName = "explorer.exe",
        UseShellExecute = false,
        CreateNoWindow = true
    };

    // 3. Use ArgumentList to prevent argument splitting & command injection
    psi.ArgumentList.Add(directoryPath);

    Process.Start(psi);
}
```

---

## 4. Image Decompression Bomb & Memory Exhaustion Defense

AOI raw images (`photo/Main*.png`) can reach `17701 × 14208` px (~1 GB uncompressed). A corrupted or malicious file with forged header dimensions (e.g. `100,000 × 100,000`) can instantly exhaust physical RAM and crash the WPF application.

### Defensive Dimension Probing:
Always read dimensions via delayed metadata before requesting full buffer decode:

```csharp
public static bool ValidateImageDimensionsSafely(Stream stream, int maxAllowedDimension = 20_000)
{
    try
    {
        // Decode header metadata ONLY (BitmapCreateOptions.DelayCreation)
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.None);

        if (decoder.Frames.Count == 0) return false;

        var frame = decoder.Frames[0];
        int pixelWidth = frame.PixelWidth;
        int pixelHeight = frame.PixelHeight;

        // Reset stream position for actual decoder
        if (stream.CanSeek) stream.Seek(0, SeekOrigin.Begin);

        return pixelWidth > 0 && pixelWidth <= maxAllowedDimension &&
               pixelHeight > 0 && pixelHeight <= maxAllowedDimension;
    }
    catch
    {
        return false;
    }
}
```

---

## 5. XML External Entity (XXE) & DTD Protection

When parsing XML payloads from `MES_1.log` (`<OTPDEFECTCODE>`, `<OTPDEFECTVALUE>`), if `XmlReader` or `XmlDocument` is used, always prohibit DTD processing:

```csharp
public static XmlReaderSettings CreateSecureXmlReaderSettings()
{
    return new XmlReaderSettings
    {
        // Prohibit external entity injection (XXE) and billion laughs DoS
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 10_000_000,
        IgnoreComments = true,
        IgnoreWhitespace = true
    };
}
```

---

## 6. Desktop Security Review Checklist

- [ ] Are all UNC paths checked to ensure they resolve strictly to private/trusted subnets?
- [ ] Does `Process.Start` set `UseShellExecute = false` and pass parameters via `ArgumentList`?
- [ ] Are camera image headers checked for reasonable pixel dimensions before decoding?
- [ ] Is DTD processing strictly prohibited (`DtdProcessing.Prohibit`) on all XML readers?
- [ ] Are log files free of credentials, tokens, or Windows session hashes?
