---
name: secure-csv-and-input-validation
description: Use this skill when dealing with CSV export generation, barcode/serial input handling, SMB network share path validation, and OWASP formula injection protection in OTPSoftware.
---

# Secure Data Export & Industrial Input Validation

This skill defines security standards to prevent data injection, path traversal, and malicious spreadsheet execution in factory environments.

## 1. CSV Formula Injection Defense (OWASP ASVS / DDE)

When operators or management open exported shift reports (`ShiftReport_{Date}_{Shift}.csv`) in Microsoft Excel or LibreOffice Calc, malicious cells starting with formula trigger characters can execute external commands or exfiltrate data via Dynamic Data Exchange (DDE).

- **Dangerous Prefixes**: `= `, `+`, `-`, `@`, `\t`, `0x09`, `\r`, `0x0D`.
- **Sanitization Rule**:
  - If a cell value starts with any of the dangerous prefixes, prepend a single quote (`'`) to force Excel to treat the cell strictly as literal text.
  - Wrap any value containing commas, quotes, or newlines in double quotes, escaping existing double quotes with `""`.
  ```csharp
  public static string SanitizeCsvCell(string? value)
  {
      if (string.IsNullOrEmpty(value)) return string.Empty;

      ReadOnlySpan<char> span = value.AsSpan();
      char firstChar = span[0];

      bool requiresPrefixEscape = firstChar is '=' or '+' or '-' or '@' or '\t' or '\r';
      bool requiresQuotes = requiresPrefixEscape || span.ContainsAny(',', '"', '\n', '\r');

      if (!requiresQuotes) return value;

      var sb = new StringBuilder();
      sb.Append('"');
      if (requiresPrefixEscape)
      {
          sb.Append('\'');
      }
      foreach (char c in span)
      {
          if (c == '"') sb.Append("\"\"");
          else sb.Append(c);
      }
      sb.Append('"');
      return sb.ToString();
  }
  ```

## 2. SMB File Path & Traversal Protection

- **No Arbitrary Path Concatenation**: When constructing folder search paths from user-entered or camera tokens (`[Status]_[Serial]_[Timestamp]_[Station]`), never allow `..`, directory separators (`/`, `\`), or wildcards in the `Serial` or `Station` fields.
- **Strict Token Validation**:
  ```csharp
  private static readonly SearchValues<char> InvalidPathChars =
      SearchValues.Create(Path.GetInvalidFileNameChars());

  public static bool IsValidToken(ReadOnlySpan<char> token) =>
      !token.IsEmpty && !token.ContainsAny(InvalidPathChars);
  ```

## 3. Checklist for Data Ingestion & Export

- [ ] Are all columns in exported shift reports sanitized using `SanitizeCsvCell`?
- [ ] Are unit serials, defect codes, and PMNT identifiers checked against invalid path/filename characters before resolving camera folder paths?
- [ ] Are automated shift report file names formatted with deterministic date/time strings (`yyyyMMdd`) rather than unsanitized external strings?
