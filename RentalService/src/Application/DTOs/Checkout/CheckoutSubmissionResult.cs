using System;
using System.Collections.Generic;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Checkout;

public sealed class CheckoutSubmissionResult
{
    private CheckoutSubmissionResult(
        bool isSuccess,
        Error error,
        CheckoutOperationAcceptedResponse? accepted = null,
        CheckoutPreparationResult? preparation = null)
    {
        IsSuccess = isSuccess;
        Error = error ?? Error.None;
        Accepted = accepted;
        FieldErrors = preparation?.FieldErrors ?? [];
        LineIssues = preparation?.LineIssues ?? [];
        CurrentQuote = preparation?.CurrentQuote;
        PendingOrderCode = preparation?.PendingOrderCode;
    }

    public CheckoutOperationAcceptedResponse Value => IsSuccess
        ? Accepted!
        : throw new InvalidOperationException("Cannot access Value on failed CheckoutSubmissionResult.");

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }
    public CheckoutOperationAcceptedResponse? Accepted { get; }

    public bool IsRetryable => Error.Type == ErrorType.ServiceUnavailable;
    public IReadOnlyList<CheckoutFieldError> FieldErrors { get; }
    public IReadOnlyList<CheckoutLineIssue> LineIssues { get; }
    public CheckoutPlan? CurrentQuote { get; }
    public string? PendingOrderCode { get; }

    public static CheckoutSubmissionResult Success(CheckoutOperationAcceptedResponse accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        return new(true, Error.None, accepted: accepted);
    }

    public static CheckoutSubmissionResult Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(false, error);
    }

    public static CheckoutSubmissionResult From(CheckoutPreparationResult preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return new(false, preparation.Error, preparation: preparation);
    }
}
