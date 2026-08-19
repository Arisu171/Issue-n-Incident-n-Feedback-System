using System.Globalization;
using System.Linq.Expressions;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Search;

/// <summary>Ghép biểu thức lambda (And/Or/Not) bằng cách thay tham số — đủ cho EF Core dịch sang SQL.</summary>
public static class PredicateBuilder
{
    public static Expression<Func<T, bool>> True<T>() => _ => true;
    public static Expression<Func<T, bool>> False<T>() => _ => false;

    public static Expression<Func<T, bool>> And<T>(this Expression<Func<T, bool>> a, Expression<Func<T, bool>> b)
        => Combine(a, b, Expression.AndAlso);

    public static Expression<Func<T, bool>> Or<T>(this Expression<Func<T, bool>> a, Expression<Func<T, bool>> b)
        => Combine(a, b, Expression.OrElse);

    public static Expression<Func<T, bool>> Not<T>(this Expression<Func<T, bool>> a)
        => Expression.Lambda<Func<T, bool>>(Expression.Not(a.Body), a.Parameters);

    private static Expression<Func<T, bool>> Combine<T>(Expression<Func<T, bool>> a, Expression<Func<T, bool>> b, Func<Expression, Expression, BinaryExpression> op)
    {
        var p = a.Parameters[0];
        var body = op(a.Body, new Replace(b.Parameters[0], p).Visit(b.Body));
        return Expression.Lambda<Func<T, bool>>(body, p);
    }

    private sealed class Replace : ExpressionVisitor
    {
        private readonly ParameterExpression _from, _to;
        public Replace(ParameterExpression from, ParameterExpression to) { _from = from; _to = to; }
        protected override Expression VisitParameter(ParameterExpression node) => node == _from ? _to : node;
    }
}

/// <summary>Ngữ cảnh biên dịch: DbContext (subquery), người tìm, thời điểm, phạm vi <c>in:</c>.</summary>
public sealed record SearchContext(AppDbContext Db, Guid UserId, string UserLogin, bool CanSeeInternal, DateTimeOffset Now, HashSet<string> In);

/// <summary>
/// Dịch AST (mục 2.7) thành <c>Expression&lt;Func&lt;Ticket,bool&gt;&gt;</c> để EF Core chạy trên read model
/// (snapshot <c>tickets</c> + <c>ticket_search_comments</c>). Qualifier lạ → 400.
/// </summary>
public static class SearchQueryCompiler
{
    public static Expression<Func<Ticket, bool>> Compile(SearchNode node, SearchContext ctx)
        => node switch
        {
            AndNode a => a.Items.Count == 0 ? PredicateBuilder.True<Ticket>() : a.Items.Select(i => Compile(i, ctx)).Aggregate((x, y) => x.And(y)),
            OrNode o => o.Items.Select(i => Compile(i, ctx)).Aggregate((x, y) => x.Or(y)),
            NotNode n => Compile(n.Inner, ctx).Not(),
            TextNode t => Text(t.Text, ctx),
            QualifierNode q => Qualifier(q.Key, q.Value, ctx),
            _ => throw AppException.BadRequest("Biểu thức tìm kiếm không hợp lệ.")
        };

    private static Expression<Func<Ticket, bool>> Text(string raw, SearchContext ctx)
    {
        var text = raw.Trim();
        if (text.Length == 0) return PredicateBuilder.True<Ticket>();
        var db = ctx.Db;
        var like = $"%{text}%";
        var scopes = ctx.In.Count == 0 ? new HashSet<string> { "title", "body" } : ctx.In;
        Expression<Func<Ticket, bool>> result = PredicateBuilder.False<Ticket>();

        if (scopes.Contains("title"))
        {
            result = result.Or(t => EF.Functions.ILike(t.Title, like));
        }
        if (scopes.Contains("body"))
        {
            result = result.Or(t => t.SearchVector!.Matches(EF.Functions.PlainToTsQuery("simple", text)) || EF.Functions.ILike(t.Body, like));
        }
        if (scopes.Contains("comments"))
        {
            var internalOk = ctx.CanSeeInternal;
            result = result.Or(t => db.TicketSearchComments.Any(c => c.TicketId == t.Id
                && (EF.Functions.ToTsVector("simple", c.PublicText).Matches(EF.Functions.PlainToTsQuery("simple", text)) || EF.Functions.ILike(c.PublicText, like)
                    || (internalOk && (EF.Functions.ToTsVector("simple", c.InternalText).Matches(EF.Functions.PlainToTsQuery("simple", text)) || EF.Functions.ILike(c.InternalText, like))))));
        }
        return result;
    }

    private static Expression<Func<Ticket, bool>> Qualifier(string key, string rawValue, SearchContext ctx)
    {
        var db = ctx.Db;
        var value = rawValue.Trim();
        var lower = value.ToLowerInvariant();
        var uid = ctx.UserId;

        switch (key)
        {
            case "is":
                return lower switch
                {
                    "open" => t => t.State == TicketState.Open,
                    "closed" => t => t.State == TicketState.Closed,
                    "locked" => t => t.IsLocked,
                    "unlocked" => t => !t.IsLocked,
                    "pinned" => t => t.IsPinned,
                    "issue" or "ticket" => PredicateBuilder.True<Ticket>(),
                    "blocked" => t => db.TicketReferences.Any(r => r.SourceId == t.Id && r.RelationType == RelationType.BlockedBy
                                                                    && db.Tickets.Any(b => b.Id == r.TargetId && b.State == TicketState.Open)),
                    _ => throw Bad(key, value)
                };
            case "state":
                return lower switch { "open" => t => t.State == TicketState.Open, "closed" => t => t.State == TicketState.Closed, _ => throw Bad(key, value) };
            case "reason":
            {
                if (!EnumNaming.TryParse<StateReason>(lower, out var reason)) throw Bad(key, value);
                return t => t.StateReason == reason;
            }
            case "type":
                return t => t.Type != null && t.Type.Name.ToLower() == lower;
            case "label":
            {
                var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => n.ToLowerInvariant()).ToList();
                if (names.Count == 0) throw Bad(key, value);
                Expression<Func<Ticket, bool>> any = PredicateBuilder.False<Ticket>();
                foreach (var name in names)
                {
                    var n = name;
                    any = any.Or(t => t.Labels.Any(l => l.Label.Name == n));
                }
                return any;
            }
            case "milestone":
                if (int.TryParse(value, out var msNumber)) return t => t.Milestone != null && t.Milestone.Number == msNumber;
                return t => t.Milestone != null && t.Milestone.Title.ToLower() == lower;
            case "assignee":
                if (value == "*") return t => t.Assignees.Any();
                { var login = Login(value, ctx); return t => t.Assignees.Any(a => a.User.Login == login); }
            case "author":
                { var login = Login(value, ctx); return t => t.Author.Login == login; }
            case "mentions":
                { var login = Login(value, ctx); return t => db.TicketEvents.Any(e => e.TicketId == t.Id && e.EventType == TicketEventTypes.Mentioned && db.Users.Any(u => u.Id == e.ActorId && u.Login == login)); }
            case "commenter":
                { var login = Login(value, ctx); return t => db.TicketEvents.Any(e => e.TicketId == t.Id && e.EventType == TicketEventTypes.Commented && db.Users.Any(u => u.Id == e.ActorId && u.Login == login)); }
            case "involves":
            {
                var login = Login(value, ctx);
                return Qualifier("author", login, ctx).Or(Qualifier("assignee", login, ctx)).Or(Qualifier("mentions", login, ctx)).Or(Qualifier("commenter", login, ctx));
            }
            case "project":
                return t => db.BoardItems.Any(i => i.TicketId == t.Id && i.Board.Name.ToLower() == lower);
            case "repo":
                return t => t.Project.Slug == lower;
            case "no":
                return lower switch
                {
                    "label" => t => !t.Labels.Any(),
                    "milestone" => t => t.MilestoneId == null,
                    "assignee" => t => !t.Assignees.Any(),
                    "type" => t => t.TypeId == null,
                    "project" => t => !db.BoardItems.Any(i => i.TicketId == t.Id),
                    "parent-issue" => t => t.ParentTicketId == null,
                    "priority" => t => t.Priority == null,
                    _ => throw Bad(key, value)
                };
            case "has":
                return lower switch
                {
                    "label" => t => t.Labels.Any(),
                    "milestone" => t => t.MilestoneId != null,
                    "assignee" => t => t.Assignees.Any(),
                    "type" => t => t.TypeId != null,
                    "project" => t => db.BoardItems.Any(i => i.TicketId == t.Id),
                    "parent-issue" => t => t.ParentTicketId != null,
                    "sub-issues" => t => db.Tickets.Any(c => c.ParentTicketId == t.Id),
                    _ => throw Bad(key, value)
                };
            case "parent-issue":
            {
                var (slug, number) = Ref(value);
                return t => t.ParentTicket != null && t.ParentTicket.Number == number && (slug == null || t.ParentTicket.Project.Slug == slug);
            }
            case "blocked-by":
            {
                var (slug, number) = Ref(value);
                return t => db.TicketReferences.Any(r => r.SourceId == t.Id && r.RelationType == RelationType.BlockedBy
                    && db.Tickets.Any(b => b.Id == r.TargetId && b.Number == number && (slug == null || b.Project.Slug == slug)));
            }
            case "linked":
                if (lower != "pr") throw Bad(key, value);
                return t => db.TicketEvents.Count(e => e.TicketId == t.Id && e.EventType == TicketEventTypes.Connected)
                            > db.TicketEvents.Count(e => e.TicketId == t.Id && e.EventType == TicketEventTypes.Disconnected);
            case "created":
                return DateRange(value, t => t.CreatedAt);
            case "updated":
                return DateRange(value, t => t.UpdatedAt);
            case "closed":
                return DateRange(value, t => t.ClosedAt ?? DateTimeOffset.MinValue).And(t => t.ClosedAt != null);
            case "comments":
                return IntRange(value, t => t.CommentsCount);
            case "reactions":
                return IntRange(value, t => db.TicketReactions.Count(r => r.TicketId == t.Id));
            case "interactions":
                return IntRange(value, t => t.CommentsCount + db.TicketReactions.Count(r => r.TicketId == t.Id));
            case "priority":
            {
                if (!EnumNaming.TryParse<TicketPriority>(lower, out var pr)) throw Bad(key, value);
                return t => t.Priority == pr;
            }
            case "sla":
            {
                var now = ctx.Now;
                var soon = now.AddHours(1);
                return lower switch
                {
                    "breached" => t => t.State == TicketState.Open && t.SlaDueAt != null && t.SlaDueAt < now && t.FirstResponseAt == null,
                    "warning" => t => t.State == TicketState.Open && t.SlaDueAt != null && t.SlaDueAt >= now && t.SlaDueAt < soon && t.FirstResponseAt == null,
                    "ok" => t => t.SlaDueAt == null || t.FirstResponseAt != null || t.SlaDueAt >= soon || t.State == TicketState.Closed,
                    _ => throw Bad(key, value)
                };
            }
            case "in":
            case "sort":
                return PredicateBuilder.True<Ticket>(); // meta — đã tách trước khi compile
            default:
                throw Bad(key, value);
        }
    }

    private static string Login(string value, SearchContext ctx)
    {
        var v = value.Trim().TrimStart('@');
        return v.Equals("me", StringComparison.OrdinalIgnoreCase) ? ctx.UserLogin : v.ToLowerInvariant();
    }

    private static (string? Slug, int Number) Ref(string value)
    {
        var text = value.Trim().TrimStart('#');
        var hash = text.IndexOf('#');
        var slug = hash > 0 ? text[..hash].ToLowerInvariant() : null;
        var num = hash > 0 ? text[(hash + 1)..] : text;
        return int.TryParse(num, out var n) ? (slug, n) : throw AppException.BadRequest($"Tham chiếu '{value}' không hợp lệ (N hoặc project#N).");
    }

    private static Expression<Func<Ticket, bool>> DateRange(string value, Expression<Func<Ticket, DateTimeOffset>> selector)
    {
        var p = selector.Parameters[0];
        Expression Cmp(Func<Expression, Expression, BinaryExpression> op, DateTimeOffset d) => op(selector.Body, Expression.Constant(d, typeof(DateTimeOffset)));
        Expression body;
        if (value.Contains(".."))
        {
            var parts = value.Split("..", 2);
            Expression? lo = parts[0] == "*" || parts[0].Length == 0 ? null : Cmp(Expression.GreaterThanOrEqual, ParseDate(parts[0], false));
            Expression? hi = parts[1] == "*" || parts[1].Length == 0 ? null : Cmp(Expression.LessThan, ParseDate(parts[1], true));
            body = lo is null && hi is null ? Expression.Constant(true) : lo is null ? hi! : hi is null ? lo : Expression.AndAlso(lo, hi);
        }
        else if (value.StartsWith(">=")) body = Cmp(Expression.GreaterThanOrEqual, ParseDate(value[2..], false));
        else if (value.StartsWith("<=")) body = Cmp(Expression.LessThan, ParseDate(value[2..], true));
        else if (value.StartsWith('>')) body = Cmp(Expression.GreaterThanOrEqual, ParseDate(value[1..], true));
        else if (value.StartsWith('<')) body = Cmp(Expression.LessThan, ParseDate(value[1..], false));
        else body = Expression.AndAlso(Cmp(Expression.GreaterThanOrEqual, ParseDate(value, false)), Cmp(Expression.LessThan, ParseDate(value, true)));
        return Expression.Lambda<Func<Ticket, bool>>(body, p);
    }

    /// <summary>YYYY-MM-DD hoặc ISO datetime; <paramref name="endOfDay"/> → ngày kế tiếp (biên trên mở).</summary>
    private static DateTimeOffset ParseDate(string text, bool endOfDay)
    {
        text = text.Trim();
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return endOfDay ? start.AddDays(1) : start;
        }
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)) return dt;
        throw AppException.BadRequest($"Ngày '{text}' không hợp lệ (YYYY-MM-DD).");
    }

    private static Expression<Func<Ticket, bool>> IntRange(string value, Expression<Func<Ticket, int>> selector)
    {
        var p = selector.Parameters[0];
        Expression Cmp(Func<Expression, Expression, BinaryExpression> op, int n) => op(selector.Body, Expression.Constant(n));
        Expression body;
        if (value.Contains(".."))
        {
            var parts = value.Split("..", 2);
            Expression? lo = parts[0] == "*" || parts[0].Length == 0 ? null : Cmp(Expression.GreaterThanOrEqual, ParseInt(parts[0]));
            Expression? hi = parts[1] == "*" || parts[1].Length == 0 ? null : Cmp(Expression.LessThanOrEqual, ParseInt(parts[1]));
            body = lo is null && hi is null ? Expression.Constant(true) : lo is null ? hi! : hi is null ? lo : Expression.AndAlso(lo, hi);
        }
        else if (value.StartsWith(">=")) body = Cmp(Expression.GreaterThanOrEqual, ParseInt(value[2..]));
        else if (value.StartsWith("<=")) body = Cmp(Expression.LessThanOrEqual, ParseInt(value[2..]));
        else if (value.StartsWith('>')) body = Cmp(Expression.GreaterThan, ParseInt(value[1..]));
        else if (value.StartsWith('<')) body = Cmp(Expression.LessThan, ParseInt(value[1..]));
        else body = Cmp(Expression.Equal, ParseInt(value));
        return Expression.Lambda<Func<Ticket, bool>>(body, p);
    }

    private static int ParseInt(string text) => int.TryParse(text.Trim(), out var n) ? n : throw AppException.BadRequest($"Số '{text}' không hợp lệ.");

    private static AppException Bad(string key, string value) => AppException.BadRequest($"Qualifier '{key}:{value}' không được hỗ trợ.");
}
