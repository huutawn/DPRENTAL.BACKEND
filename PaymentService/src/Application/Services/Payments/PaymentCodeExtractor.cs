using Dprental.PaymentService.Common.Utils;
using System.Text.RegularExpressions;

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
    private static readonly Regex CodePattern = new(
        @"(?<![\p{L}\p{N}_])" + Regex.Escape(PaymentCodeGenerator.Prefix) +
        @"[A-Z0-9]{" + PaymentCodeGenerator.CodeLength + @"}(?![\p{L}\p{N}_])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static CodeExtractionResult Extract(string? codeField, string? contentField, string? descriptionField)
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

        var descriptionCodes = FindCodes(descriptionField);
        if (descriptionCodes.Count > 1)
        {
            return new CodeExtractionResult(CodeExtractionStatus.Ambiguous, null);
        }

        if (descriptionCodes.Count == 1)
        {
            return new CodeExtractionResult(CodeExtractionStatus.SingleMatch, descriptionCodes.Single());
        }

        var contentCodes = FindCodes(contentField);

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

    private static HashSet<string> FindCodes(string? text)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(text))
        {
            foreach (Match match in CodePattern.Matches(text))
            {
                if (PaymentCodeGenerator.IsValid(match.Value))
                {
                    codes.Add(match.Value);
                }
            }
        }

        return codes;
    }
}
