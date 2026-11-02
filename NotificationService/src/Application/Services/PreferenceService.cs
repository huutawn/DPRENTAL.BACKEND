using Dprental.NotificationService.Application.DTOs;
using Dprental.NotificationService.Application.IServices;
using Dprental.NotificationService.Application.Policies;
using Dprental.NotificationService.Common;
using Dprental.NotificationService.Domain.IRepositories;

namespace Dprental.NotificationService.Application.Services;

public sealed class PreferenceService : IPreferenceService
{
    private static readonly string[] AllCategories = ["ORDER", "PAYMENT", "HANDOVER", "RETURN"];
    private readonly IPreferenceRepository _preferenceRepo;
    private readonly IUnitOfWork _unitOfWork;

    public PreferenceService(IPreferenceRepository preferenceRepo, IUnitOfWork unitOfWork)
    {
        _preferenceRepo = preferenceRepo ?? throw new ArgumentNullException(nameof(preferenceRepo));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public async Task<Result<PreferencesResponseDto>> GetPreferencesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _preferenceRepo.GetUserPreferencesAsync(userId, cancellationToken);

        var list = new List<PreferenceDto>();
        foreach (var category in AllCategories)
        {
            var match = existing.FirstOrDefault(p => string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase));
            // Mặc định an toàn là true nếu chưa lưu
            var enabled = match?.EmailEnabled ?? true;
            list.Add(new PreferenceDto(category, enabled));
        }

        return Result.Success(new PreferencesResponseDto(list));
    }

    public async Task<Result<PreferencesResponseDto>> UpdatePreferencesAsync(
        Guid userId,
        UpdatePreferencesRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (request?.Preferences == null || request.Preferences.Count == 0)
        {
            return Result.Failure<PreferencesResponseDto>(
                Error.Validation("EMPTY_PREFERENCES", "Danh sách tùy chọn preferences không được để trống."));
        }

        foreach (var pref in request.Preferences)
        {
            if (!PreferencePolicy.IsValidCategory(pref.Category))
            {
                return Result.Failure<PreferencesResponseDto>(
                    Error.Validation("INVALID_CATEGORY", $"Category '{pref.Category}' không hợp lệ. Chỉ chấp nhận: ORDER, PAYMENT, HANDOVER, RETURN."));
            }
        }

        var updatePairs = request.Preferences
            .Select(p => (p.Category.ToUpperInvariant(), p.EmailEnabled))
            .ToList();

        await _preferenceRepo.UpsertPreferencesAsync(userId, updatePairs, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GetPreferencesAsync(userId, cancellationToken);
    }
}
