using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Persistence;
using IncidentTracker.Api.Modules.Incidents;
using IncidentTracker.Api.Modules.Revisions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Feedbacks;

// ---------------- DTO ----------------

public sealed class CreateFeedbackRequest
{
    [Required]
    public FeedbackChannel Channel { get; set; }

    [EmailAddress, MaxLength(320)]
    public string? CustomerEmail { get; set; }

    [Required, MinLength(10), MaxLength(10_000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>Gắn ngay vào một sự cố đang mở. Bỏ trống thì feedback nằm ở hàng đợi chưa phân loại.</summary>
    public Guid? IncidentId { get; set; }
}

/// <summary>
/// PATCH nội dung phản hồi — mọi trường tùy chọn, <c>null</c> = không đổi.
///
/// Cố ý KHÔNG có <c>Status</c> và <c>IncidentId</c>: trạng thái tiếp nhận là hệ quả của việc
/// gắn sự cố và việc trả lời (BR-BIZ-12), còn liên kết sự cố đã có <c>POST/DELETE {id}/link</c>
/// với ràng buộc riêng. Mở đường thứ hai tới chúng ở đây là bỏ qua đúng những ràng buộc đó.
/// </summary>
public sealed class UpdateFeedbackRequest
{
    public FeedbackChannel? Channel { get; set; }

    /// <summary>Chuỗi rỗng = xóa email liên hệ; bỏ trống trường = giữ nguyên.</summary>
    [MaxLength(320)]
    public string? CustomerEmail { get; set; }

    [MinLength(10), MaxLength(10_000)]
    public string? Content { get; set; }

    /// <summary>Lý do sửa — nên điền khi sửa phản hồi của người khác.</summary>
    [MaxLength(500)]
    public string? Reason { get; set; }
}

/// <summary>Sửa một câu trả lời. Body bắt buộc — đây là PUT nội dung, không phải PATCH từng phần.</summary>
public sealed class UpdateFeedbackReplyRequest
{
    [Required, MinLength(1), MaxLength(2000)]
    public string Body { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Reason { get; set; }
}

public sealed class LinkFeedbackRequest
{
    [Required]
    public Guid IncidentId { get; set; }
}

public sealed record FeedbackResponse(
    Guid Id,
    FeedbackChannel Channel,
    string? CustomerEmail,
    string Content,
    FeedbackStatus Status,
    Guid? IncidentId,
    string? IncidentTitle,
    IncidentStatus? IncidentStatus,
    Guid CreatedBy,
    string CreatedByName,
    DateTimeOffset CreatedAt,
    /// <summary>null khi phản hồi còn nguyên bản; lịch sử đầy đủ ở <c>{id}/revisions</c>.</summary>
    EditSignature? LastEdit,
    /// <summary>Phiên bản nội dung — đặt vào <c>If-Match: "v{version}"</c> khi lưu.</summary>
    int Version);

public sealed class CreateFeedbackReplyRequest
{
    [Required, MinLength(1), MaxLength(2000)]
    public string Body { get; set; } = string.Empty;
}

/// <summary><c>Responder</c> null khi <c>IsAutomatic</c> — lời xác nhận do hệ thống sinh.</summary>
public sealed record FeedbackReplyResponse(
    Guid Id,
    Guid FeedbackId,
    UserRef? Responder,
    bool IsAutomatic,
    string Body,
    DateTimeOffset CreatedAt,
    /// <summary>Luôn null với lời xác nhận tự động — không ai sửa được nó.</summary>
    EditSignature? LastEdit,
    /// <summary>Phiên bản nội dung — đặt vào <c>If-Match: "v{version}"</c> khi lưu.</summary>
    int Version);

/// <summary>
/// Bổ sung so với mục 6.6: không có endpoint danh sách thì partial index
/// <c>where incident_id is null</c> (mục 5.6) và hàng đợi ~100 phản hồi tồn đọng của
/// Support không dùng được.
/// </summary>
public sealed class FeedbackListQuery : PagingQuery
{
    /// <summary>Tìm chữ trong nội dung phản hồi và email khách hàng.</summary>
    [MaxLength(200)]
    public string? Q { get; set; }

    /// <summary>true = chỉ hàng đợi chưa phân loại; false = chỉ feedback đã gắn; bỏ trống = tất cả.</summary>
    public bool? UnlinkedOnly { get; set; }

    public Guid? IncidentId { get; set; }
}

// ---------------- Service ----------------

/// <summary>CMP-09 Feedback Service — FR-BIZ-07, BR-BIZ-07.</summary>
public sealed class FeedbackService
{
    private readonly AppDbContext _db;
    private readonly IAuthorizationService _authorization;
    private readonly IOptionsMonitor<FeedbackOptions> _options;
    private readonly ContentRevisionService _revisions;
    private readonly EditClaimService _claims;
    private readonly ILogger<FeedbackService> _logger;

    public FeedbackService(AppDbContext db, IAuthorizationService authorization,
        IOptionsMonitor<FeedbackOptions> options, ContentRevisionService revisions,
        EditClaimService claims, ILogger<FeedbackService> logger)
    {
        _db = db;
        _authorization = authorization;
        _options = options;
        _revisions = revisions;
        _claims = claims;
        _logger = logger;
    }

    public async Task<FeedbackResponse> CreateAsync(
        CreateFeedbackRequest request, ClaimsPrincipal caller, Guid? projectId, CancellationToken ct)
    {
        var actorId = caller.GetUserId();

        if (request.IncidentId is not null)
        {
            await EnsureLinkableAsync(request.IncidentId.Value, projectId, caller, ct);
        }

        var feedback = new Feedback
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Channel = request.Channel,
            CustomerEmail = string.IsNullOrWhiteSpace(request.CustomerEmail)
                ? null
                : request.CustomerEmail.Trim().ToLowerInvariant(),
            Content = request.Content.Trim(),
            // Gắn ngay vào sự cố lúc tạo cũng là một hình thức tiếp nhận (BR-BIZ-12).
            Status = request.IncidentId is null ? FeedbackStatus.New : FeedbackStatus.Acknowledged,
            IncidentId = request.IncidentId,
            CreatedBy = actorId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Feedbacks.Add(feedback);

        // Phản hồi tự động (FR-BIZ-14): xác nhận tiếp nhận nằm ngay trong luồng hội thoại,
        // ghi cùng một SaveChanges với feedback nên không bao giờ có feedback "mồ côi" ack.
        var autoAck = _options.CurrentValue;
        if (autoAck.AutoAckEnabled)
        {
            _db.FeedbackReplies.Add(new FeedbackReply
            {
                Id = Guid.NewGuid(),
                FeedbackId = feedback.Id,
                ResponderId = null,
                IsAutomatic = true,
                Body = autoAck.AutoAckMessage,
                CreatedAt = feedback.CreatedAt
            });
        }

        await _db.SaveChangesAsync(ct);

        // NFR-SEC-02 / PII: chỉ ghi id và kênh, không bao giờ ghi content hay customer_email.
        _logger.LogInformation("Audit feedback.create actor={ActorId} feedback={FeedbackId} channel={Channel} linked={Linked} autoAck={AutoAck} result=success",
            actorId, feedback.Id, feedback.Channel, feedback.IncidentId is not null, autoAck.AutoAckEnabled);

        return await LoadAsync(feedback.Id, ct);
    }

    // ---------------- Sửa nội dung ----------------

    /// <summary>
    /// Sửa kênh, email liên hệ và nội dung phản hồi — người đã gửi nó, hoặc người có
    /// <c>feedback.respond</c>.
    ///
    /// Lời của khách hàng sửa được là chuyện phải cân nhắc, nên cái giá đi kèm được trả đủ:
    /// mỗi trường đổi để lại một dòng trong <c>content_revisions</c> với nguyên văn cũ và tên
    /// người đã thay nó. Nhân viên sửa hộ khách thì dòng đó mang <c>on_behalf</c> — đọc lịch
    /// sử là phân biệt được ngay "khách nói lại" với "nhân viên viết lại".
    /// </summary>
    public async Task<FeedbackResponse> UpdateAsync(
        Guid feedbackId, UpdateFeedbackRequest request, ClaimsPrincipal caller,
        HttpRequest http, CancellationToken ct)
    {
        var actorId = caller.GetUserId();
        var strategy = _db.Database.CreateExecutionStrategy();
        var changedFields = string.Empty;
        var onBehalf = false;

        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, ct);

            // Khóa hàng trong transaction, cùng lý do với IncidentService: người sửa sau phải
            // đọc được giá trị người sửa trước vừa ghi, nếu không old_value sẽ ghi lại một quá
            // khứ không có thật.
            var feedback = (await _db.Feedbacks
                    .FromSql($"SELECT * FROM feedbacks WHERE id = {feedbackId} FOR UPDATE")
                    .ToListAsync(ct))
                .FirstOrDefault()
                ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

            // 404 chứ không 403 khi không được đọc — cùng quyết định với LinkAsync: 403 xác
            // nhận rằng phản hồi đó có thật.
            if (!ResourceAccessRules.CanReadFeedback(caller, feedback.CreatedBy))
            {
                throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");
            }

            EnsureCanEditFeedback(feedback.CreatedBy, caller);

            // Cửa chiếm dụng — xem EditClaimService. Đặt sau khi đã khoá hàng.
            await _claims.EnsureWritableAsync(http, EditableEntityType.Feedback, feedback.Id,
                feedback.Version, actorId, "Phản hồi", ct);

            onBehalf = feedback.CreatedBy != actorId;

            var edit = _revisions.Begin(
                EditableEntityType.Feedback, feedback.Id, feedback,
                actorId, feedback.CreatedBy, request.Reason);

            if (request.Channel is { } channel)
            {
                edit.Change("channel", feedback.Channel, channel);
                feedback.Channel = channel;
            }

            if (request.CustomerEmail is not null)
            {
                var email = string.IsNullOrWhiteSpace(request.CustomerEmail)
                    ? null
                    : request.CustomerEmail.Trim().ToLowerInvariant();
                edit.Change("customer_email", feedback.CustomerEmail, email);
                feedback.CustomerEmail = email;
            }

            if (request.Content is not null)
            {
                var content = request.Content.Trim();

                // Kiểm SAU khi cắt khoảng trắng: DataAnnotations đo chuỗi gốc nên một chuỗi
                // toàn dấu cách lọt qua tầng bind rồi mới vi phạm check constraint của bảng.
                if (content.Length < 10)
                {
                    throw AppException.BadRequest("Nội dung phản hồi phải dài ít nhất 10 ký tự.");
                }

                edit.Change("content", feedback.Content, content);
                feedback.Content = content;
            }

            changedFields = edit.Record() ? edit.ChangedFields() : string.Empty;

            if (changedFields.Length > 0)
            {
                await _db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
            return true;
        });

        if (changedFields.Length > 0)
        {
            // NFR-SEC-02 / PII: chỉ ghi tên trường, không bao giờ ghi content hay customer_email.
            _logger.LogInformation(
                "Audit feedback.edit actor={ActorId} feedback={FeedbackId} fields={Fields} onBehalf={OnBehalf} result=success",
                actorId, feedbackId, changedFields, onBehalf);
        }

        return await LoadAsync(feedbackId, ct);
    }

    /// <summary>
    /// Sửa một câu trả lời — người đã viết nó, hoặc người có <c>feedback.respond</c>.
    ///
    /// Lời xác nhận tự động trả 409: nó không có tác giả để đứng tên và nó là bản sao đúng
    /// câu hệ thống đã gửi cho khách, nên sửa nó là sửa lại quá khứ.
    /// </summary>
    public async Task<FeedbackReplyResponse> UpdateReplyAsync(
        Guid feedbackId, Guid replyId, UpdateFeedbackReplyRequest request,
        ClaimsPrincipal caller, HttpRequest http, CancellationToken ct)
    {
        var actorId = caller.GetUserId();
        var body = request.Body.Trim();

        if (body.Length == 0)
        {
            throw AppException.BadRequest("Nội dung câu trả lời không được rỗng.");
        }

        var feedback = await _db.Feedbacks.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

        await EnsureAsync(feedback, caller, ResourceOperationRequirement.Read,
            "Phản hồi này không do bạn gửi.");

        var strategy = _db.Database.CreateExecutionStrategy();
        var changed = false;

        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, ct);

            var reply = (await _db.FeedbackReplies
                    .FromSql($"SELECT * FROM feedback_replies WHERE id = {replyId} FOR UPDATE")
                    .ToListAsync(ct))
                .FirstOrDefault();

            if (reply is null || reply.FeedbackId != feedbackId)
            {
                throw AppException.NotFound($"Không tìm thấy câu trả lời '{replyId}'.");
            }

            EnsureCanEditReply(reply, caller);

            // Cửa chiếm dụng — xem EditClaimService. Đặt sau khi đã khoá hàng.
            await _claims.EnsureWritableAsync(http, EditableEntityType.FeedbackReply, reply.Id,
                reply.Version, actorId, "Câu trả lời", ct);

            var edit = _revisions.Begin(
                EditableEntityType.FeedbackReply, reply.Id, reply,
                actorId, reply.ResponderId, request.Reason);

            edit.Change("body", reply.Body, body);
            reply.Body = body;

            changed = edit.Record();
            if (changed)
            {
                await _db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
            return true;
        });

        if (changed)
        {
            // NFR-SEC-02: không ghi body vào log.
            _logger.LogInformation(
                "Audit feedback.reply.edit actor={ActorId} feedback={FeedbackId} reply={ReplyId} result=success",
                actorId, feedbackId, replyId);
        }

        _db.ChangeTracker.Clear();
        var updated = await _db.FeedbackReplies.AsNoTracking()
            .Include(r => r.Responder)
            .Include(r => r.LastEditor)
            .FirstAsync(r => r.Id == replyId, ct);

        return ToReplyResponse(updated);
    }

    /// <summary>
    /// Lịch sử sửa của một phản hồi. Ràng buộc đọc y hệt bản ghi cha: giá trị cũ của
    /// <c>content</c> và <c>customer_email</c> là PII đầy đủ, nên để hở đường này là để hở
    /// đúng thứ BR-BIZ-11 che.
    /// </summary>
    public async Task<IReadOnlyList<RevisionResponse>> GetRevisionsAsync(
        Guid feedbackId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var feedback = await _db.Feedbacks.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

        await EnsureAsync(feedback, caller, ResourceOperationRequirement.Read,
            "Phản hồi này không do bạn gửi.");

        return await _revisions.ListAsync(EditableEntityType.Feedback, feedbackId, ct);
    }

    /// <summary>Lịch sử sửa của một câu trả lời — ràng buộc đọc đi theo phản hồi cha.</summary>
    public async Task<IReadOnlyList<RevisionResponse>> GetReplyRevisionsAsync(
        Guid feedbackId, Guid replyId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var feedback = await _db.Feedbacks.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

        await EnsureAsync(feedback, caller, ResourceOperationRequirement.Read,
            "Phản hồi này không do bạn gửi.");

        var exists = await _db.FeedbackReplies.AsNoTracking()
            .AnyAsync(r => r.Id == replyId && r.FeedbackId == feedbackId, ct);

        if (!exists)
        {
            throw AppException.NotFound($"Không tìm thấy câu trả lời '{replyId}'.");
        }

        return await _revisions.ListAsync(EditableEntityType.FeedbackReply, replyId, ct);
    }

    // ---------------- Chỗ sửa ----------------

    /// <summary>Nhận hoặc gia hạn chỗ sửa trên một phản hồi — cùng điều kiện với đường PATCH.</summary>
    public async Task<EditClaimResponse> ClaimFeedbackEditAsync(
        Guid feedbackId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var feedback = await LoadForClaimAsync(feedbackId, caller, ct);
        EnsureCanEditFeedback(feedback.CreatedBy, caller);

        return await _claims.ClaimAsync(
            EditableEntityType.Feedback, feedbackId, feedback.Version, caller.GetUserId(), ct);
    }

    /// <summary>Nhả chỗ sửa — chỉ xoá hàng của chính người gọi, nên không đòi quyền sửa.</summary>
    public async Task ReleaseFeedbackEditAsync(
        Guid feedbackId, ClaimsPrincipal caller, CancellationToken ct)
    {
        await LoadForClaimAsync(feedbackId, caller, ct);
        await _claims.ReleaseAsync(EditableEntityType.Feedback, feedbackId, caller.GetUserId(), ct);
    }

    public async Task<EditClaimResponse> ClaimReplyEditAsync(
        Guid feedbackId, Guid replyId, ClaimsPrincipal caller, CancellationToken ct)
    {
        await LoadForClaimAsync(feedbackId, caller, ct);

        var reply = await _db.FeedbackReplies.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == replyId && r.FeedbackId == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy câu trả lời '{replyId}'.");

        EnsureCanEditReply(reply, caller);

        return await _claims.ClaimAsync(
            EditableEntityType.FeedbackReply, replyId, reply.Version, caller.GetUserId(), ct);
    }

    public async Task ReleaseReplyEditAsync(
        Guid feedbackId, Guid replyId, ClaimsPrincipal caller, CancellationToken ct)
    {
        await LoadForClaimAsync(feedbackId, caller, ct);
        await _claims.ReleaseAsync(EditableEntityType.FeedbackReply, replyId, caller.GetUserId(), ct);
    }

    private async Task<Feedback> LoadForClaimAsync(
        Guid feedbackId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var feedback = await _db.Feedbacks.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

        await EnsureAsync(feedback, caller, ResourceOperationRequirement.Read,
            "Phản hồi này không do bạn gửi.");

        return feedback;
    }

    /// <summary>
    /// Một cửa duy nhất cho câu hỏi "người này sửa được phản hồi này không" — đường PATCH và
    /// đường giữ chỗ phải hẹp bằng nhau, nếu không thì chiếm được chỗ mà không ghi được.
    /// </summary>
    private static void EnsureCanEditFeedback(Guid createdBy, ClaimsPrincipal caller)
    {
        if (!ResourceAccessRules.CanEditFeedback(caller, createdBy))
        {
            throw new AppException(
                StatusCodes.Status403Forbidden,
                "Không đủ quyền",
                "Chỉ người đã gửi phản hồi mới sửa được nội dung của nó, hoặc cần permission "
                + $"'{Permissions.FeedbackRespond}'.",
                new Dictionary<string, object?>
                {
                    ["requiredPermission"] = Permissions.FeedbackRespond
                });
        }
    }

    private static void EnsureCanEditReply(FeedbackReply reply, ClaimsPrincipal caller)
    {
        if (reply.IsAutomatic)
        {
            throw AppException.Conflict(
                "Lời xác nhận tự động là bản sao đúng câu hệ thống đã gửi cho khách nên không sửa được.");
        }

        if (!ResourceAccessRules.CanEditFeedbackReply(caller, reply.ResponderId, reply.IsAutomatic))
        {
            throw new AppException(
                StatusCodes.Status403Forbidden,
                "Không đủ quyền",
                "Chỉ người đã viết câu trả lời mới sửa được, hoặc cần permission "
                + $"'{Permissions.FeedbackRespond}'.",
                new Dictionary<string, object?>
                {
                    ["requiredPermission"] = Permissions.FeedbackRespond
                });
        }
    }

    /// <summary>
    /// API-Feedback-Link · BR-BIZ-07. Idempotent với cùng incidentId; gắn vào sự cố đã
    /// <c>Resolved</c> trả 409 và giữ nguyên trạng thái cũ của feedback (US-BIZ-04/AC-02).
    ///
    /// Ranh giới đọc được soát ở <b>cả hai</b> đầu: phản hồi phải là của mình (trừ khi có
    /// <c>feedback.read.all</c>) và sự cố phải là sự cố mình đọc được. Thiếu vế thứ nhất thì
    /// bất kỳ ai cầm <c>feedback.link</c> cũng gắn được phản hồi của người khác vào sự cố của
    /// mình, và câu trả lời của endpoint sẽ trả nguyên nội dung phản hồi đó về (BR-SEC-01).
    /// </summary>
    public async Task<FeedbackResponse> LinkAsync(
        Guid feedbackId, Guid incidentId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var actorId = caller.GetUserId();

        var feedback = await _db.Feedbacks.FirstOrDefaultAsync(f => f.Id == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

        // 404 chứ không 403: 403 xác nhận rằng phản hồi đó có thật, tức vẫn rò một bit thông tin.
        if (!ResourceAccessRules.CanReadFeedback(caller, feedback.CreatedBy))
        {
            throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");
        }

        if (feedback.IncidentId == incidentId)
        {
            return await LoadAsync(feedbackId, ct);
        }

        await EnsureLinkableAsync(incidentId, feedback.ProjectId, caller, ct);

        feedback.IncidentId = incidentId;

        // BR-BIZ-12: gắn vào sự cố nghĩa là đã có người tiếp nhận. Không hạ cấp
        // Responded — câu trả lời của con người là mốc cao hơn việc phân loại.
        if (feedback.Status == FeedbackStatus.New)
        {
            feedback.Status = FeedbackStatus.Acknowledged;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit feedback.link actor={ActorId} feedback={FeedbackId} incident={IncidentId} result=success",
            actorId, feedbackId, incidentId);

        return await LoadAsync(feedbackId, ct);
    }

    /// <summary>
    /// Gỡ phản hồi khỏi sự cố đang gắn — thao tác sửa sai của việc phân loại.
    ///
    /// Cùng ranh giới đọc như <see cref="LinkAsync"/>: 404 chứ không 403, để không xác nhận rằng
    /// một phản hồi mình không được đọc là có thật.
    ///
    /// Idempotent: gỡ một phản hồi vốn chưa gắn gì thì trả về nguyên trạng, không báo lỗi. Người
    /// dùng bấm hai lần, hoặc hai tab cùng mở, không phải là chuyện đáng dựng lỗi.
    /// </summary>
    public async Task<FeedbackResponse> UnlinkAsync(Guid feedbackId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var actorId = caller.GetUserId();

        var feedback = await _db.Feedbacks.FirstOrDefaultAsync(f => f.Id == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

        if (!ResourceAccessRules.CanReadFeedback(caller, feedback.CreatedBy))
        {
            throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");
        }

        if (feedback.IncidentId is null)
        {
            return await LoadAsync(feedbackId, ct);
        }

        ClearIncidentLink(feedback);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit feedback.unlink actor={ActorId} feedback={FeedbackId} result=success",
            actorId, feedbackId);

        return await LoadAsync(feedbackId, ct);
    }

    /// <summary>
    /// Gỡ liên kết sự cố và đưa trạng thái về đúng thực tế.
    ///
    /// <c>Acknowledged</c> chính là cách hệ thống nói "đã gắn vào một sự cố" (xem
    /// <see cref="LinkAsync"/>). Gỡ liên kết mà giữ nguyên trạng thái sẽ để lại hàng dữ liệu mà
    /// mọi bộ lọc và mọi màn hình đọc thành "đã phân loại", trong khi cột sự cố trống trơn.
    ///
    /// <c>Responded</c> giữ nguyên: một con người đã trả lời thật, và việc đó không phụ thuộc
    /// vào chuyện phản hồi thuộc sự cố nào.
    /// </summary>
    private static void ClearIncidentLink(Feedback feedback)
    {
        feedback.IncidentId = null;
        if (feedback.Status == FeedbackStatus.Acknowledged)
        {
            feedback.Status = FeedbackStatus.New;
        }
    }

    // ---------------- UC-BIZ-09 · Trả lời phản hồi ----------------

    /// <summary>
    /// Luồng trả lời của một phản hồi, thứ tự thời gian tăng dần — gồm cả lời xác nhận
    /// tự động. Ranh giới đọc đi theo feedback cha (BR-BIZ-11).
    /// </summary>
    public async Task<IReadOnlyList<FeedbackReplyResponse>> ListRepliesAsync(
        Guid feedbackId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var feedback = await _db.Feedbacks.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

        await EnsureAsync(feedback, caller, ResourceOperationRequirement.Read,
            "Phản hồi này không do bạn gửi.");

        var rows = await _db.FeedbackReplies.AsNoTracking()
            .Include(r => r.Responder)
            .Include(r => r.LastEditor)
            .Where(r => r.FeedbackId == feedbackId)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync(ct);

        return rows.Select(ToReplyResponse).ToList();
    }

    /// <summary>
    /// FR-BIZ-14 — doanh nghiệp trả lời phản hồi và feedback chuyển sang <c>Responded</c>.
    /// Trả lời tiếp một feedback đã <c>Responded</c> vẫn hợp lệ: hội thoại không đóng.
    /// </summary>
    public async Task<FeedbackReplyResponse> ReplyAsync(
        Guid feedbackId, CreateFeedbackReplyRequest request, ClaimsPrincipal caller, CancellationToken ct)
    {
        var actorId = caller.GetUserId();

        var feedback = await _db.Feedbacks.FirstOrDefaultAsync(f => f.Id == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

        await EnsureAsync(feedback, caller, ResourceOperationRequirement.Write,
            "Phản hồi này nằm ngoài phạm vi bạn được trả lời.");

        var reply = new FeedbackReply
        {
            Id = Guid.NewGuid(),
            FeedbackId = feedbackId,
            ResponderId = actorId,
            IsAutomatic = false,
            Body = request.Body.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.FeedbackReplies.Add(reply);
        feedback.Status = FeedbackStatus.Responded;
        await _db.SaveChangesAsync(ct);

        // NFR-SEC-02: không ghi body vào log.
        _logger.LogInformation(
            "Audit feedback.respond actor={ActorId} feedback={FeedbackId} reply={ReplyId} result=success",
            actorId, feedbackId, reply.Id);

        var responder = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == actorId, ct);
        reply.Responder = responder;
        return ToReplyResponse(reply);
    }

    /// <summary>
    /// BR-BIZ-11 — người không có <c>feedback.read.all</c> chỉ thấy phản hồi do chính mình
    /// gửi. Nội dung phản hồi và email liên hệ đều là PII (mục 5.7) nên đây không chỉ là
    /// chuyện tiện dụng.
    /// </summary>
    public async Task<PagedResult<FeedbackResponse>> ListAsync(
        Guid? projectId, FeedbackListQuery query, ClaimsPrincipal caller, CancellationToken ct)
    {
        // Lọc theo project trong câu truy vấn, trước bộ lọc quyền sở hữu — lọc sau thì con số
        // tổng của phân trang đã tính trên dữ liệu project khác.
        var q = BaseQuery().Where(f => f.ProjectId == projectId);

        if (ResourceAccessRules.FeedbackVisibilityFilter(caller) is { } visibility)
        {
            q = q.Where(visibility);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = "%" + query.Q.Trim() + "%";
            q = q.Where(f => EF.Functions.ILike(f.Content, pattern)
                || (f.CustomerEmail != null && EF.Functions.ILike(f.CustomerEmail, pattern)));
        }

        if (query.UnlinkedOnly == true)
        {
            q = q.Where(f => f.IncidentId == null);
        }
        else if (query.UnlinkedOnly == false)
        {
            q = q.Where(f => f.IncidentId != null);
        }

        if (query.IncidentId is not null)
        {
            q = q.Where(f => f.IncidentId == query.IncidentId.Value);
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(f => f.CreatedAt).ThenBy(f => f.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(ct);

        return new PagedResult<FeedbackResponse>(
            items.Select(ToResponse).ToList(), query.Page, query.PageSize, total);
    }

    public async Task<FeedbackResponse> GetAsync(Guid id, ClaimsPrincipal caller, CancellationToken ct)
    {
        var feedback = await BaseQuery().FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{id}'.");

        await EnsureAsync(feedback, caller, ResourceOperationRequirement.Read,
            "Phản hồi này không do bạn gửi.");

        return ToResponse(feedback);
    }

    /// <summary>Một cửa duy nhất dịch kết quả resource authorization sang lỗi HTTP 403.</summary>
    private async Task EnsureAsync(Feedback feedback, ClaimsPrincipal caller,
        ResourceOperationRequirement requirement, string detail)
    {
        var result = await _authorization.AuthorizeAsync(caller, feedback, requirement);

        if (!result.Succeeded)
        {
            throw new AppException(
                StatusCodes.Status403Forbidden,
                "Không đủ quyền",
                detail,
                new Dictionary<string, object?>
                {
                    ["requiredPermission"] = Permissions.FeedbackReadAll
                });
        }
    }

    /// <summary>
    /// BR-BIZ-07 — chỉ gắn được vào Incident tồn tại, chưa xóa mềm và chưa Resolved.
    ///
    /// Kèm cả ràng buộc đọc: nếu không kiểm, một khách hàng có thể dò id và móc phản hồi của
    /// mình vào sự cố của khách hàng khác — cùng họ lỗi BOLA, chỉ khác là ở chiều ghi.
    /// </summary>
    /// <summary>
    /// Chuyển phản hồi sang project khác.
    ///
    /// Phản hồi **đã gắn vào một sự cố** vẫn chuyển được, nhưng liên kết đó bị gỡ trong cùng thao
    /// tác. Bất biến "phản hồi và sự cố cùng project" được giữ bằng cách bỏ liên kết, chứ không
    /// bằng cách từ chối chuyển — sự cố ở lại project cũ, phản hồi đi, và không còn gì trỏ chéo.
    ///
    /// Trước đây ở đây trả 409 và bảo người dùng "chuyển sự cố thì phản hồi tự theo". Đúng về bất
    /// biến nhưng sai về nghiệp vụ: một phản hồi bị phân loại nhầm project thường cũng bị gắn
    /// nhầm sự cố, và bắt phải chuyển cả sự cố — thứ có thể đang đúng chỗ — để sửa một phản hồi
    /// là bắt sửa cái không hỏng.
    ///
    /// Muốn phản hồi đi theo sự cố thì vẫn dùng <c>IncidentService.TransferAsync</c> như cũ; ở
    /// đó liên kết được giữ nguyên vì cả hai cùng sang.
    /// </summary>
    public async Task<FeedbackResponse> TransferAsync(
        Guid feedbackId, Guid? fromProjectId, Guid? toProjectId, Guid actorId, CancellationToken ct)
    {
        var feedback = await _db.Feedbacks.FirstOrDefaultAsync(f => f.Id == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

        if (feedback.ProjectId != fromProjectId)
        {
            throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}' trong project này.");
        }

        // Gỡ liên kết TRƯỚC khi đổi project, trong cùng một lần lưu: sự cố ở lại project cũ nên
        // giữ liên kết là dựng ra đúng thứ bất biến cấm — phản hồi một nơi, sự cố một nơi.
        var unlinked = feedback.IncidentId is not null;
        if (unlinked)
        {
            ClearIncidentLink(feedback);
        }

        feedback.ProjectId = toProjectId;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Audit feedback.transfer actor={ActorId} feedback={FeedbackId} to={ProjectId} unlinked={Unlinked}",
            actorId, feedbackId, toProjectId, unlinked);

        return ToResponse(await BaseQuery().FirstAsync(f => f.Id == feedbackId, ct));
    }

    private async Task EnsureLinkableAsync(
        Guid incidentId, Guid? feedbackProjectId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var incident = await _db.Incidents.AsNoTracking()
            .Select(i => new { i.Id, i.Status, i.ReporterId, i.AssigneeId, i.ProjectId })
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy sự cố '{incidentId}'.");

        // Phản hồi và sự cố nó gắn vào phải **cùng project**. Đây là bất biến, không phải sở
        // thích: mọi màn hình đều hỏi dữ liệu theo project, nên một phản hồi ở project A trỏ sang
        // sự cố ở project B là thứ không màn hình nào biết bày ở đâu — và
        // <c>TransferAsync</c> dựa hẳn vào nó để kéo phản hồi đi theo sự cố. Người có quyền toàn
        // cục chạm được cả hai project nên nếu không chặn ở đây thì chính họ làm vỡ bất biến.
        //
        // 404 chứ không 409, cùng lý do đã áp cho lỗ hổng BOLA: 409 xác nhận sự cố đó có thật.
        if (incident.ProjectId != feedbackProjectId)
        {
            throw AppException.NotFound($"Không tìm thấy sự cố '{incidentId}' trong project này.");
        }

        if (!ResourceAccessRules.CanAttachFeedbackTo(caller, incident.ReporterId, incident.AssigneeId))
        {
            throw AppException.Forbidden(
                "Sự cố này không do bạn báo cáo và cũng không được giao cho bạn nên không thể gắn phản hồi vào.");
        }

        if (incident.Status == Domain.IncidentStatus.Resolved)
        {
            throw AppException.Conflict(
                "Sự cố đã đóng nên không nhận thêm phản hồi. Hãy tạo sự cố mới hoặc để phản hồi ở hàng đợi chưa phân loại.",
                new Dictionary<string, object?> { ["currentStatus"] = incident.Status.ToString() });
        }
    }

    private IQueryable<Feedback> BaseQuery()
        => _db.Feedbacks.AsNoTracking()
            .Include(f => f.Incident)
            .Include(f => f.CreatedByUser)
            .Include(f => f.LastEditor);

    private async Task<FeedbackResponse> LoadAsync(Guid id, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var feedback = await BaseQuery().FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{id}'.");
        return ToResponse(feedback);
    }

    private static FeedbackResponse ToResponse(Feedback f)
        => new(f.Id, f.Channel, f.CustomerEmail, f.Content, f.Status, f.IncidentId,
            f.Incident?.Title, f.Incident?.Status,
            f.CreatedBy, f.CreatedByUser?.DisplayName ?? string.Empty, f.CreatedAt,
            ContentRevisionService.SignatureOf(f.LastEditedAt, f.LastEditor),
            f.Version);

    private static FeedbackReplyResponse ToReplyResponse(FeedbackReply r)
        => new(r.Id, r.FeedbackId,
            r.Responder is null ? null : new UserRef(r.Responder.Id, r.Responder.DisplayName, r.Responder.Email),
            r.IsAutomatic, r.Body, r.CreatedAt,
            ContentRevisionService.SignatureOf(r.LastEditedAt, r.LastEditor),
            r.Version);
}

// ---------------- Controller ----------------

/// <summary>CMP-07 Feedback Controller — UC-BIZ-04.</summary>
[ApiController]
[Route("api/projects/{project}/feedbacks")]
[Produces("application/json")]
public sealed class FeedbacksController : ControllerBase
{
    private readonly FeedbackService _feedbacks;
    private readonly Persistence.AppDbContext _db;

    public FeedbacksController(FeedbackService feedbacks, Persistence.AppDbContext db)
    {
        _feedbacks = feedbacks;
        _db = db;
    }

    private Task<Guid?> ScopeAsync(string project, CancellationToken ct)
        => Tickets.Organization.VirtualProject.ResolveAsync(_db, project, User, ct);

    /// <summary>
    /// Phản hồi này có nằm trong project trên đường dẫn không.
    ///
    /// 404 chứ không 403: 403 xác nhận phản hồi đó có thật, tức vẫn rò một bit thông tin — cùng
    /// lý do đã áp cho lỗ hổng BOLA ở <c>LinkAsync</c>.
    /// </summary>
    private async Task EnsureInScopeAsync(string project, Guid id, CancellationToken ct)
    {
        var projectId = await ScopeAsync(project, ct);
        var actual = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            _db.Feedbacks.Where(f => f.Id == id).Select(f => new { f.ProjectId }), ct);

        if (actual is null || actual.ProjectId != projectId)
        {
            throw Common.AppException.NotFound($"Không tìm thấy phản hồi '{id}' trong project này.");
        }
    }

    /// <summary>
    /// Hàng đợi phân loại của Support, đồng thời là danh sách "phản hồi của tôi" với khách
    /// hàng: người không có <c>feedback.read.all</c> chỉ nhận về phản hồi do chính mình gửi
    /// (BR-BIZ-11), nên PII của khách khác không bao giờ tới tay họ.
    /// </summary>
    [HttpGet]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(typeof(PagedResult<FeedbackResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<FeedbackResponse>>> List(
        string project, [FromQuery] FeedbackListQuery query, CancellationToken ct)
        => Ok(await _feedbacks.ListAsync(await ScopeAsync(project, ct), query, User, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(typeof(FeedbackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FeedbackResponse>> Get(string project, Guid id, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _feedbacks.GetAsync(id, User, ct));
    }

    /// <summary>API-Feedback-Create.</summary>
    [HttpPost]
    [RequirePermission(Permissions.FeedbackCreate)]
    [ProducesResponseType(typeof(FeedbackResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FeedbackResponse>> Create(
        string project, CreateFeedbackRequest request, CancellationToken ct)
    {
        var created = await _feedbacks.CreateAsync(request, User, await ScopeAsync(project, ct), ct);
        return CreatedAtAction(nameof(Get), new { project, id = created.Id }, created);
    }

    /// <summary>API-Feedback-Link.</summary>
    [HttpPost("{id:guid}/link")]
    [RequirePermission(Permissions.FeedbackLink)]
    [ProducesResponseType(typeof(FeedbackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FeedbackResponse>> Link(
        string project, Guid id, LinkFeedbackRequest request, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _feedbacks.LinkAsync(id, request.IncidentId, User, ct));
    }

    /// <summary>
    /// API-Feedback-Unlink — gỡ phản hồi khỏi sự cố đang gắn.
    ///
    /// Cùng quyền với việc gắn: ai phân loại được thì cũng phải sửa sai được. Tách quyền riêng
    /// sẽ tạo ra vai trò gắn được mà không gỡ được — một cái bẫy chứ không phải một ranh giới.
    /// </summary>
    [HttpDelete("{id:guid}/link")]
    [RequirePermission(Permissions.FeedbackLink)]
    [ProducesResponseType(typeof(FeedbackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FeedbackResponse>> Unlink(string project, Guid id, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _feedbacks.UnlinkAsync(id, User, ct));
    }

    /// <summary>Chuyển phản hồi sang project khác — thao tác phân loại.</summary>
    [HttpPost("{id:guid}/transfer")]
    [RequirePermission(Permissions.ProjectMemberManage)]
    [ProducesResponseType(typeof(FeedbackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FeedbackResponse>> Transfer(
        string project, Guid id, Incidents.TransferProjectRequest request, CancellationToken ct)
    {
        var from = await ScopeAsync(project, ct);
        var to = await Tickets.Organization.VirtualProject.ResolveAsync(_db, request.ToProject, User, ct);
        return Ok(await _feedbacks.TransferAsync(id, from, to, User.GetUserId(), ct));
    }

    /// <summary>API-Feedback-Replies — luồng trả lời, gồm cả lời xác nhận tự động.</summary>
    [HttpGet("{id:guid}/replies")]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(typeof(IReadOnlyList<FeedbackReplyResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<FeedbackReplyResponse>>> Replies(
        string project, Guid id, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _feedbacks.ListRepliesAsync(id, User, ct));
    }

    /// <summary>API-Feedback-Respond · FR-BIZ-14 — doanh nghiệp trả lời khách hàng.</summary>
    [HttpPost("{id:guid}/replies")]
    [RequirePermission(Permissions.FeedbackRespond)]
    [ProducesResponseType(typeof(FeedbackReplyResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FeedbackReplyResponse>> Reply(
        string project, Guid id, CreateFeedbackReplyRequest request, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        var created = await _feedbacks.ReplyAsync(id, request, User, ct);
        return CreatedAtAction(nameof(Replies), new { project, id }, created);
    }

    /// <summary>API-Feedback-Update — sửa nội dung phản hồi, nguyên văn cũ ở lại trong lịch sử.</summary>
    [HttpPatch("{id:guid}")]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(typeof(FeedbackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FeedbackResponse>> Update(
        string project, Guid id, UpdateFeedbackRequest request, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        var updated = await _feedbacks.UpdateAsync(id, request, User, Request, ct);
        Tickets.Infrastructure.EntityTags.SetETag(Response, updated.Version);
        return Ok(updated);
    }

    /// <summary>API-Feedback-EditClaim — "tôi đang mở form sửa phản hồi này".</summary>
    [HttpPut("{id:guid}/edit-claim")]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(typeof(EditClaimResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EditClaimResponse>> ClaimEdit(
        string project, Guid id, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _feedbacks.ClaimFeedbackEditAsync(id, User, ct));
    }

    /// <summary>API-Feedback-EditClaim-Release.</summary>
    [HttpDelete("{id:guid}/edit-claim")]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReleaseEdit(string project, Guid id, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        await _feedbacks.ReleaseFeedbackEditAsync(id, User, ct);
        return NoContent();
    }

    /// <summary>API-Feedback-Revisions — ai đã sửa gì, từ giá trị nào sang giá trị nào.</summary>
    [HttpGet("{id:guid}/revisions")]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(typeof(IReadOnlyList<RevisionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RevisionResponse>>> Revisions(
        string project, Guid id, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _feedbacks.GetRevisionsAsync(id, User, ct));
    }

    /// <summary>API-Feedback-Reply-Update — sửa câu trả lời đã gửi.</summary>
    [HttpPatch("{id:guid}/replies/{replyId:guid}")]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(typeof(FeedbackReplyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FeedbackReplyResponse>> UpdateReply(
        string project, Guid id, Guid replyId, UpdateFeedbackReplyRequest request, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _feedbacks.UpdateReplyAsync(id, replyId, request, User, Request, ct));
    }

    /// <summary>API-Feedback-Reply-EditClaim.</summary>
    [HttpPut("{id:guid}/replies/{replyId:guid}/edit-claim")]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(typeof(EditClaimResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EditClaimResponse>> ClaimReplyEdit(
        string project, Guid id, Guid replyId, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _feedbacks.ClaimReplyEditAsync(id, replyId, User, ct));
    }

    /// <summary>API-Feedback-Reply-EditClaim-Release.</summary>
    [HttpDelete("{id:guid}/replies/{replyId:guid}/edit-claim")]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReleaseReplyEdit(
        string project, Guid id, Guid replyId, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        await _feedbacks.ReleaseReplyEditAsync(id, replyId, User, ct);
        return NoContent();
    }

    /// <summary>API-Feedback-Reply-Revisions.</summary>
    [HttpGet("{id:guid}/replies/{replyId:guid}/revisions")]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(typeof(IReadOnlyList<RevisionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RevisionResponse>>> ReplyRevisions(
        string project, Guid id, Guid replyId, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _feedbacks.GetReplyRevisionsAsync(id, replyId, User, ct));
    }
}
