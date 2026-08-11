using System.ComponentModel.DataAnnotations;

namespace IncidentTracker.Api.Common;

/// <summary>Kết quả phân trang chuẩn cho mọi list endpoint (mục 6.6).</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>pageSize mặc định 20, tối đa 100; vượt ngưỡng trả 400 (TC-BIZ-09).</summary>
public class PagingQuery
{
    public const int MaxPageSize = 100;

    [Range(1, int.MaxValue, ErrorMessage = "page phải >= 1.")]
    public int Page { get; set; } = 1;

    [Range(1, MaxPageSize, ErrorMessage = "pageSize phải nằm trong khoảng 1..100.")]
    public int PageSize { get; set; } = 20;
}
