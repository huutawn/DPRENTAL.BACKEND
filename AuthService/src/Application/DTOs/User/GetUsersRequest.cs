namespace AuthService.Domain.DTOs.User;

public class GetUsersRequest
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Search { get; set; }
    public string? Type { get; set; }
}