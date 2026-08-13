using System.ComponentModel.DataAnnotations;

namespace IncidentTracker.Api.Modules.Feedbacks;

/// <summary>
/// Cấu hình tiếp nhận phản hồi — trọng tâm là lời xác nhận tự động (auto-acknowledge).
///
/// Đây là mức "phản hồi tự động" làm được mà không cần hạ tầng gửi email: ngay khi phản hồi
/// được ghi nhận, hệ thống chèn một câu trả lời tự động vào chính luồng hội thoại của phản
/// hồi đó, nên khách hàng mở lại là thấy "đã có người nhận". Nó cố ý KHÔNG đổi trạng thái
/// feedback: xác nhận đã nhận chưa phải là câu trả lời của con người, trạng thái vẫn là
/// <c>New</c> để hàng đợi phân loại của Support không mất dấu.
/// </summary>
public sealed class FeedbackOptions
{
    public const string SectionName = "Feedback";

    /// <summary>Bật/tắt lời xác nhận tự động khi ghi nhận phản hồi mới.</summary>
    public bool AutoAckEnabled { get; set; } = true;

    /// <summary>Nội dung lời xác nhận tự động — cấu hình được để mỗi bản triển khai tự xưng danh.</summary>
    [Required, MaxLength(2000)]
    public string AutoAckMessage { get; set; } =
        "Chúng tôi đã tiếp nhận phản hồi của bạn và sẽ phản hồi trong thời gian sớm nhất. "
        + "Bạn có thể theo dõi trạng thái xử lý ngay tại trang này.";
}
