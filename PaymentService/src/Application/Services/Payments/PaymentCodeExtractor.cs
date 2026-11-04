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
    private static readonly Regex CodePattern = BuildPattern(PaymentCodeGenerator.Prefix);
    private static readonly (Regex Pattern, string Prefix)[] OutgoingPatterns =
    [
        (BuildPattern(PaymentCodeGenerator.PayoutPrefix), PaymentCodeGenerator.PayoutPrefix),
        (BuildPattern(PaymentCodeGenerator.RefundPrefix), PaymentCodeGenerator.RefundPrefix)
    ];

    private static Regex BuildPattern(string prefix) => new(
        @"(?<![\p{L}\p{N}_])" + Regex.Escape(prefix) +
        @"[A-Z0-9]{" + PaymentCodeGenerator.CodeLength + @"}(?![\p{L}\p{N}_])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    // Tiền ra: field code của SePay chỉ nhận tiền tố DPR nên mã DPO/DRF chỉ đọc từ description/content.
    // Gặp hơn một mã (kể cả một DPO và một DRF) thì Ambiguous để admin xem tay.
    public static CodeExtractionResult ExtractOutgoingCode(string? contentField, string? descriptionField)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (pattern, prefix) in OutgoingPatterns)
        {
            codes.UnionWith(FindCodes(descriptionField, pattern, prefix));
            codes.UnionWith(FindCodes(contentField, pattern, prefix));
        }
        return codes.Count switch
        {
            0 => new CodeExtractionResult(CodeExtractionStatus.Missing, null),
            1 => new CodeExtractionResult(CodeExtractionStatus.SingleMatch, codes.Single()),
            _ => new CodeExtractionResult(CodeExtractionStatus.Ambiguous, null)
        };
    }

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

        var descriptionCodes = FindCodes(descriptionField, CodePattern, PaymentCodeGenerator.Prefix);
        if (descriptionCodes.Count > 1)
        {
            return new CodeExtractionResult(CodeExtractionStatus.Ambiguous, null);
        }

        if (descriptionCodes.Count == 1)
        {
            return new CodeExtractionResult(CodeExtractionStatus.SingleMatch, descriptionCodes.Single());
        }

        var contentCodes = FindCodes(contentField, CodePattern, PaymentCodeGenerator.Prefix);

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

    private static HashSet<string> FindCodes(string? text, Regex pattern, string prefix)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(text))
        {
            foreach (Match match in pattern.Matches(text))
            {
                if (PaymentCodeGenerator.IsValid(match.Value, prefix))
                {
                    codes.Add(match.Value);
                }
            }
        }

        return codes;
    }
}
