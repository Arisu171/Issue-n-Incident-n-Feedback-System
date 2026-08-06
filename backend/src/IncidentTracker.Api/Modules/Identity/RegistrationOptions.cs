using System.ComponentModel.DataAnnotations;

namespace IncidentTracker.Api.Modules.Identity;

/// <summary>
/// Cấu hình tự đăng ký tài khoản.
///
/// Bản thiết kế v1.0 chỉ cho admin tạo tài khoản, nhưng như vậy người dùng mới bị chặn hoàn
/// toàn cho tới khi liên hệ được admin. Đăng ký mở ra một lối tự phục vụ, đổi lại phải chấp
/// nhận rằng người lạ có thể tạo tài khoản — nên mọi lựa chọn mặc định ở đây đều theo hướng
/// đặc quyền tối thiểu.
/// </summary>
public sealed class RegistrationOptions
{
    public const string SectionName = "Registration";

    /// <summary>Cho phép tự đăng ký. Đặt false để quay về mô hình chỉ admin tạo tài khoản.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Chỉ nhận email thuộc các tên miền này, phân tách bằng dấu phẩy — ví dụ
    /// <c>"cong-ty.vn,chi-nhanh.vn"</c>. Bỏ trống là nhận mọi tên miền: tiện cho môi trường học
    /// tập nhưng <b>không nên</b> dùng khi hệ thống phơi ra Internet, vì đây mới là hàng rào
    /// thật sự ngăn người ngoài tổ chức tạo tài khoản.
    ///
    /// Kiểu chuỗi chứ không phải danh sách là có chủ đích: binder của
    /// <c>Microsoft.Extensions.Configuration</c> chỉ dựng được danh sách từ khóa đánh số
    /// (<c>__0</c>, <c>__1</c>), nên một biến môi trường duy nhất trong <c>.env</c> sẽ không
    /// bind được nếu để kiểu <c>List&lt;string&gt;</c>.
    /// </summary>
    public string AllowedEmailDomains { get; set; } = string.Empty;

    /// <summary>
    /// Bật thì tài khoản mới được tạo ở trạng thái vô hiệu hóa và phải chờ admin kích hoạt.
    /// Vẫn tốt hơn mô hình cũ: người dùng tự tạo được tài khoản, admin chỉ cần bấm kích hoạt
    /// thay vì phải nhập hộ email, đặt mật khẩu rồi tìm cách gửi lại cho họ.
    /// </summary>
    public bool RequireApproval { get; set; }

    /// <summary>
    /// Vai trò gán cho tài khoản tự đăng ký. Mặc định <c>customer</c>: đây chính là đường
    /// khách hàng tự lập tài khoản để gửi sự cố và phản hồi (ACT-BIZ-03). Vai trò này tạo
    /// được dữ liệu nhưng chỉ đọc lại được đúng những gì mình đã gửi.
    ///
    /// Danh sách này bị lọc qua <see cref="AssignableRoles"/> nên dù có cấu hình sai cũng không
    /// thể tự cấp quyền quản trị — kể cả khi một vai trò trong đó bị bỏ đi (như <c>viewer</c>),
    /// kết quả tệ nhất là tài khoản mới không có vai trò nào, không phải có thừa vai trò.
    /// </summary>
    public string DefaultRoles { get; set; } = "customer";

    /// <summary>Mật khẩu tối thiểu. Đường đăng ký công khai nên đặt cao hơn đường admin tạo.</summary>
    [Range(8, 128)]
    public int MinPasswordLength { get; set; } = 8;

    /// <summary>
    /// Chặn cứng: tự đăng ký không bao giờ được nhận vai trò ngoài danh sách này, bất kể cấu
    /// hình. Nếu thiếu chặn này thì một lỗi đánh máy trong biến môi trường đủ để biến đường
    /// đăng ký công khai thành đường leo thang đặc quyền.
    /// </summary>
    public static readonly IReadOnlySet<string> AssignableRoles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "customer" };

    private static IReadOnlyList<string> Split(string value) => value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => x.ToLowerInvariant())
        .Distinct()
        .ToList();

    /// <summary>Danh sách tên miền đã tách.</summary>
    public IReadOnlyList<string> AllowedDomainList() => Split(AllowedEmailDomains);

    /// <summary>Vai trò thực sự được gán sau khi lọc.</summary>
    public IReadOnlyList<string> SafeDefaultRoles() =>
        Split(DefaultRoles).Where(AssignableRoles.Contains).ToList();

    /// <summary>Vai trò bị cấu hình nhưng đã bị chặn — dùng để ghi cảnh báo lúc khởi động.</summary>
    public IReadOnlyList<string> RejectedRoles() =>
        Split(DefaultRoles).Where(r => !AssignableRoles.Contains(r)).ToList();

    public bool IsEmailDomainAllowed(string email)
    {
        var domains = AllowedDomainList();
        if (domains.Count == 0)
        {
            return true;
        }

        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
        {
            return false;
        }

        var domain = email[(at + 1)..];
        return domains.Any(d =>
            string.Equals(d.TrimStart('@'), domain, StringComparison.OrdinalIgnoreCase));
    }
}
