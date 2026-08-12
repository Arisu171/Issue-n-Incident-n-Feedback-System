using System.Security.Claims;
using System.Text.Json;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Organization;

/// <summary>
/// UC-09 / BR-ORG-07 (Architecture v3.1) — Board ≈ GitHub Projects rút gọn: cột = option của trường
/// Status, item = ticket (bất kỳ project) hoặc draft, automation đóng → cột Done.
/// </summary>
public sealed class BoardService
{
    public static readonly string[] DefaultColumns = { "Todo", "In Progress", "Done" };

    private readonly AppDbContext _db;
    private readonly TicketQueries _q;
    private readonly TicketEventStore _events;
    private readonly TimeProvider _clock;

    public BoardService(AppDbContext db, TicketQueries q, TicketEventStore events, TimeProvider clock)
    {
        _db = db;
        _q = q;
        _events = events;
        _clock = clock;
    }

    public async Task<IReadOnlyList<BoardResponse>> ListAsync(bool includeClosed, ClaimsPrincipal user, CancellationToken ct)
    {
        IQueryable<Board> q = _db.Boards.AsNoTracking().Include(b => b.Columns);

        // Board cố ý xuyên project, nên nó phải tự lọc: chỉ hiện board **chung** (chưa có thẻ nào)
        // hoặc board có ít nhất một thẻ thuộc project người xem với tới. Hiện đủ board mà quên lọc
        // thẻ thì tiêu đề ticket của project bị cấm vẫn lộ ra — xem ToResponseAsync.
        if (user.ProjectsWith(Permissions.TicketRead) is { } slugs)
        {
            q = q.Where(b => !b.Items.Any() || b.Items.Any(i => i.Ticket != null && slugs.Contains(i.Ticket.Project.Slug)));
        }

        var rows = await (includeClosed ? q : q.Where(b => !b.IsClosed)).OrderBy(b => b.Name).ToListAsync(ct);
        var list = new List<BoardResponse>();
        foreach (var b in rows) list.Add(await ToResponseAsync(b, ct));
        return list;
    }

    public async Task<BoardDetailResponse> GetAsync(Guid id, CancellationToken ct)
    {
        var board = await FindAsync(id, ct);
        var items = await _db.BoardItems.AsNoTracking()
            .Include(i => i.Ticket)!.ThenInclude(t => t!.Project)
            .Include(i => i.Ticket)!.ThenInclude(t => t!.Labels).ThenInclude(l => l.Label)
            .Include(i => i.Ticket)!.ThenInclude(t => t!.Assignees).ThenInclude(a => a.User)
            .Where(i => i.BoardId == id)
            .OrderBy(i => i.ColumnId).ThenBy(i => i.Position).ToListAsync(ct);
        return new BoardDetailResponse(await ToResponseAsync(board, ct), items.Select(ToItem).ToList());
    }

    public async Task<BoardResponse> CreateAsync(BoardRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var board = new Board
        {
            Id = Guid.NewGuid(), Name = request.Name.Trim(), Description = request.Description?.Trim(),
            Visibility = request.Visibility ?? BoardVisibility.Internal, CreatedById = user.GetUserId(), CreatedAt = _clock.GetUtcNow()
        };
        for (var i = 0; i < DefaultColumns.Length; i++)
        {
            board.Columns.Add(new BoardColumn { Id = Guid.NewGuid(), BoardId = board.Id, Name = DefaultColumns[i], Position = i });
        }
        var done = board.Columns.Last();
        board.Automation = request.Automation is { } a && a.ValueKind == JsonValueKind.Object
            ? a.GetRawText()
            : JsonSerializer.Serialize(new { item_closed_to_column_id = done.Id, auto_add_query = (string?)null });
        _db.Boards.Add(board);
        await _db.SaveChangesAsync(ct);
        return await ToResponseAsync(board, ct);
    }

    public async Task<BoardResponse> UpdateAsync(Guid id, BoardRequest request, CancellationToken ct)
    {
        var board = await FindAsync(id, ct);
        board.Name = request.Name.Trim();
        if (request.Description is not null) board.Description = request.Description.Trim();
        if (request.Visibility is { } v) board.Visibility = v;
        if (request.IsClosed is { } c) board.IsClosed = c;
        if (request.Automation is { } a)
        {
            if (a.ValueKind != JsonValueKind.Object) throw AppException.BadRequest("automation phải là object.");
            if (a.TryGetProperty("item_closed_to_column_id", out var col) && col.ValueKind == JsonValueKind.String
                && Guid.TryParse(col.GetString(), out var colId) && board.Columns.All(x => x.Id != colId))
            {
                throw AppException.BadRequest("item_closed_to_column_id không thuộc board này.");
            }
            board.Automation = a.GetRawText();
        }
        await _db.SaveChangesAsync(ct);
        return await ToResponseAsync(board, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var board = await FindAsync(id, ct);
        _db.Boards.Remove(board);
        await _db.SaveChangesAsync(ct);
    }

    // ---- columns ----

    public async Task<BoardResponse> AddColumnAsync(Guid boardId, BoardColumnRequest request, CancellationToken ct)
    {
        var board = await FindAsync(boardId, ct);
        var name = request.Name.Trim();
        if (board.Columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new AppException(StatusCodes.Status422UnprocessableEntity, "Cột đã tồn tại", $"Cột '{name}' đã có trên board.");
        var position = request.Position ?? (board.Columns.Count == 0 ? 0 : board.Columns.Max(c => c.Position) + 1);
        board.Columns.Add(new BoardColumn { Id = Guid.NewGuid(), BoardId = board.Id, Name = name, Color = request.Color, Position = position });
        await _db.SaveTranslatingConflictAsync("Cột đã tồn tại.", ct);
        return await ToResponseAsync(board, ct);
    }

    public async Task<BoardResponse> UpdateColumnAsync(Guid boardId, Guid columnId, BoardColumnRequest request, CancellationToken ct)
    {
        var board = await FindAsync(boardId, ct);
        var column = board.Columns.FirstOrDefault(c => c.Id == columnId) ?? throw AppException.NotFound("Không tìm thấy cột.");
        column.Name = request.Name.Trim();
        if (request.Color is not null) column.Color = request.Color;
        if (request.Position is { } p) column.Position = p;
        await _db.SaveTranslatingConflictAsync("Cột đã tồn tại.", ct);
        return await ToResponseAsync(board, ct);
    }

    /// <summary>
    /// Xóa cột — chỉ khi cột đã trống.
    ///
    /// Trước đây xóa lúc nào cũng được: khoá ngoại đặt <c>SetNull</c> nên thẻ trong cột rơi về
    /// "Chưa phân cột". Không mất dữ liệu, nhưng cũng không ai thấy chúng đi đâu — một cú bấm
    /// nhầm là cả cột thẻ biến khỏi chỗ đang nhìn. Bắt làm trống trước thì việc chuyển thẻ đi đâu
    /// là quyết định của người dùng, có thể nhìn thấy được.
    ///
    /// Chặn ở đây chứ không chỉ ở giao diện: endpoint vẫn gọi thẳng được, và khi đó hành vi
    /// <c>SetNull</c> sẽ âm thầm xảy ra khác hẳn điều màn hình nói.
    /// </summary>
    public async Task<BoardResponse> DeleteColumnAsync(Guid boardId, Guid columnId, CancellationToken ct)
    {
        var board = await FindAsync(boardId, ct);
        var column = board.Columns.FirstOrDefault(c => c.Id == columnId) ?? throw AppException.NotFound("Không tìm thấy cột.");

        var remaining = await _db.BoardItems.CountAsync(i => i.ColumnId == columnId, ct);
        if (remaining > 0)
        {
            throw new AppException(StatusCodes.Status409Conflict, "Cột chưa trống",
                $"Cột '{column.Name}' còn {remaining} thẻ. Chuyển hết sang cột khác rồi mới xóa được.",
                new Dictionary<string, object?> { ["remaining_items"] = remaining });
        }

        board.Columns.Remove(column);
        _db.BoardColumns.Remove(column);
        await _db.SaveChangesAsync(ct);
        return await ToResponseAsync(board, ct);
    }

    // ---- items ----

    public async Task<BoardItemResponse> AddItemAsync(Guid boardId, BoardItemRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        var itemId = await _q.InTransactionAsync(async () =>
        {
            var board = await FindAsync(boardId, ct);
            var columnId = request.ColumnId ?? board.Columns.OrderBy(c => c.Position).FirstOrDefault()?.Id;
            if (columnId is { } cid && board.Columns.All(c => c.Id != cid)) throw AppException.BadRequest("column_id không thuộc board.");

            Ticket? ticket = null;
            if (!string.IsNullOrWhiteSpace(request.Ticket))
            {
                ticket = Guid.TryParse(request.Ticket, out var guid)
                    ? await _q.LoadByIdAsync(guid, true, ct)
                    : await _q.ResolveRefAsync(request.Ticket, TicketSeeder.DefaultProjectSlug, ct);
                if (await _db.BoardItems.AnyAsync(i => i.BoardId == boardId && i.TicketId == ticket.Id, ct))
                    throw AppException.Conflict("Ticket đã có trên board này (BR-ORG-07).");
                await _q.LockTicketRowAsync(ticket.Id, ct);
                ticket = await _q.LoadByIdAsync(ticket.Id, true, ct);
            }
            else if (string.IsNullOrWhiteSpace(request.DraftTitle))
            {
                throw AppException.BadRequest("Cần ticket hoặc draft_title.");
            }

            var position = await NextPositionAsync(boardId, columnId, ct);
            var item = new BoardItem
            {
                Id = Guid.NewGuid(), BoardId = boardId, TicketId = ticket?.Id, DraftTitle = ticket is null ? request.DraftTitle!.Trim() : null,
                ColumnId = columnId, Position = position, AddedAt = now
            };
            _db.BoardItems.Add(item);
            if (ticket is not null)
            {
                await _events.AppendAsync(ticket, TicketEventTypes.AddedToBoard, new { board_id = boardId, board_name = board.Name, column_id = columnId }, actorId, at: now, ct: ct);
            }
            await _db.SaveChangesAsync(ct);
            return item.Id;
        }, ct);

        return await ItemAsync(itemId, ct);
    }

    public async Task<BoardItemResponse> MoveItemAsync(Guid boardId, Guid itemId, MoveBoardItemRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        await _q.InTransactionAsync(async () =>
        {
            var board = await FindAsync(boardId, ct);
            var item = await _db.BoardItems.FirstOrDefaultAsync(i => i.Id == itemId && i.BoardId == boardId, ct) ?? throw AppException.NotFound("Không tìm thấy item.");
            var targetColumn = request.ColumnId ?? item.ColumnId;
            if (targetColumn is { } tc && board.Columns.All(c => c.Id != tc)) throw AppException.BadRequest("column_id không thuộc board.");

            var from = item.ColumnId;
            item.ColumnId = targetColumn;
            // Sắp lại vị trí trong cột đích.
            var siblings = await _db.BoardItems.Where(i => i.BoardId == boardId && i.ColumnId == targetColumn && i.Id != itemId).OrderBy(i => i.Position).ToListAsync(ct);
            var index = request.Position is { } p ? Math.Clamp(p, 0, siblings.Count) : siblings.Count;
            siblings.Insert(index, item);
            for (var i = 0; i < siblings.Count; i++) siblings[i].Position = i;

            if (item.TicketId is { } tid && from != targetColumn)
            {
                await _q.LockTicketRowAsync(tid, ct);
                var ticket = await _q.LoadByIdAsync(tid, true, ct);
                await _events.AppendAsync(ticket, TicketEventTypes.BoardColumnChanged,
                    new { board_id = boardId, board_name = board.Name, from_column_id = from, to_column_id = targetColumn,
                          from_column = board.Columns.FirstOrDefault(c => c.Id == from)?.Name, to_column = board.Columns.FirstOrDefault(c => c.Id == targetColumn)?.Name },
                    actorId, at: now, ct: ct);
            }
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);

        return await ItemAsync(itemId, ct);
    }

    public async Task RemoveItemAsync(Guid boardId, Guid itemId, ClaimsPrincipal user, CancellationToken ct)
    {
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();
        await _q.InTransactionAsync(async () =>
        {
            var board = await FindAsync(boardId, ct);
            var item = await _db.BoardItems.FirstOrDefaultAsync(i => i.Id == itemId && i.BoardId == boardId, ct) ?? throw AppException.NotFound("Không tìm thấy item.");
            _db.BoardItems.Remove(item);
            if (item.TicketId is { } tid)
            {
                await _q.LockTicketRowAsync(tid, ct);
                var ticket = await _q.LoadByIdAsync(tid, true, ct);
                await _events.AppendAsync(ticket, TicketEventTypes.RemovedFromBoard, new { board_id = boardId, board_name = board.Name }, actorId, at: now, ct: ct);
            }
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    /// <summary>Template <c>projects[]</c> / auto-add: thêm ticket vào board theo tên nếu chưa có (không SaveChanges).</summary>
    public async Task AddToBoardsByNameAsync(Ticket ticket, IEnumerable<string> boardNames, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        var names = boardNames.Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 0).Distinct().ToList();
        if (names.Count == 0) return;
        var boards = await _db.Boards.Include(b => b.Columns).Where(b => !b.IsClosed && names.Contains(b.Name.ToLower())).ToListAsync(ct);
        foreach (var board in boards)
        {
            await AddTicketToBoardAsync(board, ticket, actorId, now, ct);
        }
    }

    private async Task AddTicketToBoardAsync(Board board, Ticket ticket, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        if (await _db.BoardItems.AnyAsync(i => i.BoardId == board.Id && i.TicketId == ticket.Id, ct)
            || _db.BoardItems.Local.Any(i => i.BoardId == board.Id && i.TicketId == ticket.Id)) return;
        var column = board.Columns.OrderBy(c => c.Position).FirstOrDefault()?.Id;
        _db.BoardItems.Add(new BoardItem
        {
            Id = Guid.NewGuid(), BoardId = board.Id, TicketId = ticket.Id, ColumnId = column,
            Position = await NextPositionAsync(board.Id, column, ct), AddedAt = now
        });
        await _events.AppendAsync(ticket, TicketEventTypes.AddedToBoard, new { board_id = board.Id, board_name = board.Name, column_id = column, automation = actorId is null }, actorId, at: now, ct: ct);
    }

    /// <summary>BR-ORG-07 <c>auto_add_query</c>: board tự thêm ticket khớp Query DSL (chạy nền sau mỗi event).</summary>
    internal async Task ApplyAutoAddAsync(Guid ticketId, Search.SearchService search, System.Security.Claims.ClaimsPrincipal system, CancellationToken ct)
    {
        // jsonb không lọc bằng Contains trong SQL → lấy board đang mở (ít) rồi đọc automation trong bộ nhớ.
        var boards = (await _db.Boards.Include(b => b.Columns).Where(b => !b.IsClosed).ToListAsync(ct))
            .Where(b => b.Automation.Contains("auto_add_query", StringComparison.Ordinal)).ToList();
        if (boards.Count == 0) return;
        Ticket? ticket = null;
        var now = _clock.GetUtcNow();
        foreach (var board in boards)
        {
            using var automation = JsonDocument.Parse(board.Automation);
            if (!automation.RootElement.TryGetProperty("auto_add_query", out var q) || q.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(q.GetString())) continue;
            IQueryable<Ticket> filtered;
            try
            {
                filtered = await search.ApplyAsync(_db.Tickets.Where(t => t.Id == ticketId), q.GetString()!, system, ct);
            }
            catch (AppException)
            {
                continue; // query sai cú pháp: bỏ qua board này
            }
            if (!await filtered.AnyAsync(ct)) continue;
            ticket ??= await _q.LoadByIdAsync(ticketId, true, ct);
            await AddTicketToBoardAsync(board, ticket, null, now, ct);
        }
        if (ticket is not null) await _db.SaveChangesAsync(ct);
    }

    /// <summary>Automation: ticket CLOSED → cột <c>item_closed_to_column_id</c>; REOPENED từ cột Done → cột đầu.</summary>
    internal async Task ApplyAutomationAsync(Guid ticketId, string eventType, CancellationToken ct)
    {
        var items = await _db.BoardItems.Include(i => i.Board).ThenInclude(b => b.Columns).Where(i => i.TicketId == ticketId).ToListAsync(ct);
        if (items.Count == 0) return;
        var now = _clock.GetUtcNow();
        Ticket? ticket = null;

        foreach (var item in items)
        {
            using var automation = JsonDocument.Parse(item.Board.Automation);
            if (!automation.RootElement.TryGetProperty("item_closed_to_column_id", out var col) || col.ValueKind != JsonValueKind.String || !Guid.TryParse(col.GetString(), out var doneId))
                continue;
            if (item.Board.Columns.All(c => c.Id != doneId)) continue;

            Guid? target = eventType switch
            {
                TicketEventTypes.Closed => doneId,
                TicketEventTypes.Reopened when item.ColumnId == doneId => item.Board.Columns.OrderBy(c => c.Position).FirstOrDefault()?.Id,
                _ => null
            };
            if (target is null || target == item.ColumnId) continue;

            var from = item.ColumnId;
            item.ColumnId = target;
            item.Position = await NextPositionAsync(item.BoardId, target, ct);

            ticket ??= await _q.LoadByIdAsync(ticketId, true, ct);
            await _events.AppendAsync(ticket, TicketEventTypes.BoardColumnChanged,
                new { board_id = item.BoardId, board_name = item.Board.Name, from_column_id = from, to_column_id = target,
                      from_column = item.Board.Columns.FirstOrDefault(c => c.Id == from)?.Name, to_column = item.Board.Columns.FirstOrDefault(c => c.Id == target)?.Name, automation = true },
                null, at: now, ct: ct);
        }
        await _db.SaveChangesAsync(ct);
    }

    // ---- helpers ----

    private async Task<int> NextPositionAsync(Guid boardId, Guid? columnId, CancellationToken ct)
        => (await _db.BoardItems.Where(i => i.BoardId == boardId && i.ColumnId == columnId).MaxAsync(i => (int?)i.Position, ct) ?? -1) + 1;

    private async Task<Board> FindAsync(Guid id, CancellationToken ct)
        => await _db.Boards.Include(b => b.Columns).FirstOrDefaultAsync(b => b.Id == id, ct)
           ?? throw AppException.NotFound("Không tìm thấy board.");

    private async Task<BoardItemResponse> ItemAsync(Guid itemId, CancellationToken ct)
    {
        var item = await _db.BoardItems.AsNoTracking()
            .Include(i => i.Ticket)!.ThenInclude(t => t!.Project)
            .Include(i => i.Ticket)!.ThenInclude(t => t!.Labels).ThenInclude(l => l.Label)
            .Include(i => i.Ticket)!.ThenInclude(t => t!.Assignees).ThenInclude(a => a.User)
            .FirstAsync(i => i.Id == itemId, ct);
        return ToItem(item);
    }

    private async Task<BoardResponse> ToResponseAsync(Board b, CancellationToken ct)
    {
        var counts = await _db.BoardItems.Where(i => i.BoardId == b.Id).GroupBy(i => i.ColumnId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var creator = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == b.CreatedById, ct);
        var columns = b.Columns.OrderBy(c => c.Position)
            .Select(c => new BoardColumnResponse(c.Id, c.Name, c.Color, c.Position, counts.FirstOrDefault(x => x.Key == c.Id)?.Count ?? 0)).ToList();
        return new BoardResponse(b.Id, b.Name, b.Description, b.Visibility, JsonDocument.Parse(b.Automation).RootElement.Clone(), b.IsClosed,
            creator is null ? null : UserSummary.From(creator), b.CreatedAt, columns, counts.Sum(x => x.Count));
    }

    private static BoardItemResponse ToItem(BoardItem i)
        => new(i.Id, i.ColumnId, i.Position, i.DraftTitle,
            i.Ticket is null ? null : new TicketRefResponse(i.Ticket.Id, i.Ticket.Project.Slug, i.Ticket.Number, i.Ticket.Title, i.Ticket.State, i.Ticket.StateReason),
            i.Ticket?.Labels.Select(l => TicketQueries.ToResponse(l.Label)).ToList() ?? new List<LabelResponse>(),
            i.Ticket?.Assignees.Select(a => UserSummary.From(a.User)).ToList() ?? new List<UserSummary>(),
            i.AddedAt);
}

/// <summary>Consumer: automation board khi ticket đóng/mở lại (chạy nền, không chặn API).</summary>
public sealed class BoardAutomationConsumer : IConsumer<TicketEventAppended>
{
    private static readonly HashSet<string> AutoAddTriggers = new(StringComparer.Ordinal)
    {
        TicketEventTypes.Opened, TicketEventTypes.Labeled, TicketEventTypes.Typed, TicketEventTypes.Assigned, TicketEventTypes.Milestoned,
        TicketEventTypes.PriorityChanged, TicketEventTypes.Reopened, TicketEventTypes.Renamed
    };

    private readonly BoardService _boards;
    private readonly Search.SearchService _search;
    private readonly AppDbContext _db;

    public BoardAutomationConsumer(BoardService boards, Search.SearchService search, AppDbContext db)
    {
        _boards = boards;
        _search = search;
        _db = db;
    }

    public async Task Consume(ConsumeContext<TicketEventAppended> context)
    {
        var msg = context.Message;
        if (msg.EventType is TicketEventTypes.Closed or TicketEventTypes.Reopened)
        {
            await _boards.ApplyAutomationAsync(msg.TicketId, msg.EventType, context.CancellationToken);
        }
        if (AutoAddTriggers.Contains(msg.EventType))
        {
            // Query chạy với danh tính "hệ thống" mức Admin để không bị lọc visibility.
            var admin = await _db.Users.AsNoTracking().Where(u => u.UserRoles.Any(r => r.Role.Name == "admin")).OrderBy(u => u.CreatedAt).FirstOrDefaultAsync(context.CancellationToken);
            if (admin is null) return;
            var identity = new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim(AppClaimTypes.Subject, admin.Id.ToString()),
                new System.Security.Claims.Claim(AppClaimTypes.Permission, Permissions.TicketRead),
                new System.Security.Claims.Claim(AppClaimTypes.Permission, Permissions.TicketInternalNote),
                new System.Security.Claims.Claim(AppClaimTypes.Permission, Permissions.TicketDelete)
            }, "system");
            await _boards.ApplyAutoAddAsync(msg.TicketId, _search, new System.Security.Claims.ClaimsPrincipal(identity), context.CancellationToken);
        }
    }
}

[ApiController]
[Route("api/boards")]
[Produces("application/json")]
public sealed class BoardsController : ControllerBase
{
    private readonly BoardService _boards;
    public BoardsController(BoardService boards) => _boards = boards;

    [HttpGet]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<IReadOnlyList<BoardResponse>>> List([FromQuery] bool includeClosed, CancellationToken ct)
        => Ok(await _boards.ListAsync(includeClosed, User, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<BoardDetailResponse>> Get(Guid id, CancellationToken ct)
        => Ok(await _boards.GetAsync(id, ct));

    [HttpPost]
    [RequirePermission(Permissions.BoardWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<BoardResponse>> Create(BoardRequest request, CancellationToken ct)
    {
        var created = await _boards.CreateAsync(request, User, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:guid}")]
    [RequirePermission(Permissions.BoardWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<BoardResponse>> Update(Guid id, BoardRequest request, CancellationToken ct)
        => Ok(await _boards.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.BoardWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _boards.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/columns")]
    [RequirePermission(Permissions.BoardWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<BoardResponse>> AddColumn(Guid id, BoardColumnRequest request, CancellationToken ct)
        => Ok(await _boards.AddColumnAsync(id, request, ct));

    [HttpPatch("{id:guid}/columns/{columnId:guid}")]
    [RequirePermission(Permissions.BoardWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<BoardResponse>> UpdateColumn(Guid id, Guid columnId, BoardColumnRequest request, CancellationToken ct)
        => Ok(await _boards.UpdateColumnAsync(id, columnId, request, ct));

    [HttpDelete("{id:guid}/columns/{columnId:guid}")]
    [RequirePermission(Permissions.BoardWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<BoardResponse>> DeleteColumn(Guid id, Guid columnId, CancellationToken ct)
        => Ok(await _boards.DeleteColumnAsync(id, columnId, ct));

    /// <summary>Thêm ticket (Triage+) hoặc draft item.</summary>
    [HttpPost("{id:guid}/items")]
    [RequirePermission(Permissions.TicketTriage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<BoardItemResponse>> AddItem(Guid id, BoardItemRequest request, CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await _boards.AddItemAsync(id, request, User, ct));

    /// <summary>Kéo-thả: đổi cột / vị trí → <c>BOARD_COLUMN_CHANGED</c>.</summary>
    [HttpPatch("{id:guid}/items/{itemId:guid}")]
    [RequirePermission(Permissions.TicketTriage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<BoardItemResponse>> MoveItem(Guid id, Guid itemId, MoveBoardItemRequest request, CancellationToken ct)
        => Ok(await _boards.MoveItemAsync(id, itemId, request, User, ct));

    [HttpDelete("{id:guid}/items/{itemId:guid}")]
    [RequirePermission(Permissions.TicketTriage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> RemoveItem(Guid id, Guid itemId, CancellationToken ct)
    {
        await _boards.RemoveItemAsync(id, itemId, User, ct);
        return NoContent();
    }
}
