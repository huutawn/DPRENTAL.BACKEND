using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Common.Pagination;

public class PageRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "Page must be greater than 0.")]
    public int Page { get; set; } = 1;

    [Range(1, 100, ErrorMessage = "PageSize must be between 1 and 100.")]
    public int PageSize { get; set; } = 20;

    public PageRequest() { }

    public PageRequest(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }
}