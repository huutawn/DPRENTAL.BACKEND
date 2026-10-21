using Dprental.PaymentService.Common.Utils;

namespace Dprental.PaymentService.Application.Services;

public enum CodeExtractionStatus
{
    SingleMatch,
    Missing,
    Ambiguous
}

public sealed record CodeExtractionResult(CodeExtractionStatus Status, string? Code);

public static class PaymentCodeExtractor
{
    private const string CodePrefix = "DPR ";
    private const int FullCodeLength = 14;

    public static CodeExtractionResult Extract(string? codeField, string? contentField)
    {
        string? validCodeFromField = null;
        bool hasInvalidCodeField = false;

        // null or empty string is considered no code provided
        if (!string.IsNullOrEmpty(codeField))
        {
            // Do not trim: exact check on raw string
            if (PaymentCodeGenerator.IsValid(codeField))
            {
                validCodeFromField = codeField;
            }
            else
            {
                hasInvalidCodeField = true;
            }
        }

        var contentCodes = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(contentField))
        {
            int startIndex = 0;
            while (startIndex <= contentField.Length - FullCodeLength)
            {
                int prefixIndex = contentField.IndexOf(CodePrefix, startIndex, StringComparison.Ordinal);
                if (prefixIndex < 0 || prefixIndex + FullCodeLength > contentField.Length)
                {
                    break;
                }

                // Check boundary before: must be start of string or whitespace
                bool beforeOk = prefixIndex == 0 || char.IsWhiteSpace(contentField[prefixIndex - 1]);
                // Check boundary after: must be end of string or whitespace
                bool afterOk = prefixIndex + FullCodeLength == contentField.Length ||
                               char.IsWhiteSpace(contentField[prefixIndex + FullCodeLength]);

                if (beforeOk && afterOk)
                {
                    string candidate = contentField.Substring(prefixIndex, FullCodeLength);
                    if (PaymentCodeGenerator.IsValid(candidate))
                    {
                        contentCodes.Add(candidate);
                    }
                }

                startIndex = prefixIndex + 1;
            }
        }

        // If multiple distinct codes found in content -> Ambiguous
        if (contentCodes.Count > 1)
        {
            return new CodeExtractionResult(CodeExtractionStatus.Ambiguous, null);
        }

        // If code field was non-empty but invalid format
        if (hasInvalidCodeField)
        {
            if (contentCodes.Count > 0)
            {
                // Code field contradicts content -> Ambiguous/Conflict
                return new CodeExtractionResult(CodeExtractionStatus.Ambiguous, null);
            }

            // Neither source has a valid code -> Missing
            return new CodeExtractionResult(CodeExtractionStatus.Missing, null);
        }

        string? validContentCode = contentCodes.Count == 1 ? contentCodes.First() : null;

        if (validCodeFromField is not null && validContentCode is not null)
        {
            if (string.Equals(validCodeFromField, validContentCode, StringComparison.Ordinal))
            {
                return new CodeExtractionResult(CodeExtractionStatus.SingleMatch, validCodeFromField);
            }

            // Sources conflict
            return new CodeExtractionResult(CodeExtractionStatus.Ambiguous, null);
        }

        if (validCodeFromField is not null)
        {
            return new CodeExtractionResult(CodeExtractionStatus.SingleMatch, validCodeFromField);
        }

        if (validContentCode is not null)
        {
            return new CodeExtractionResult(CodeExtractionStatus.SingleMatch, validContentCode);
        }

        return new CodeExtractionResult(CodeExtractionStatus.Missing, null);
    }
}
