using System.Text;
using IncidentTracker.Api.Common;

namespace IncidentTracker.Api.Modules.Tickets.Search;

/// <summary>AST của Query DSL (Architecture v3.1, mục 2.7).</summary>
public abstract record SearchNode;
public sealed record AndNode(IReadOnlyList<SearchNode> Items) : SearchNode;
public sealed record OrNode(IReadOnlyList<SearchNode> Items) : SearchNode;
public sealed record NotNode(SearchNode Inner) : SearchNode;
public sealed record TextNode(string Text) : SearchNode;
public sealed record QualifierNode(string Key, string Value) : SearchNode;

/// <summary>
/// Tokenizer + parser cho cú pháp kiểu GitHub: <c>is:open label:bug,urgent -author:@me "lỗi đăng nhập" (a OR b)</c>.
/// Qualifier nối nhau = AND; <c>AND</c>/<c>OR</c> tường minh; <c>-</c> phủ định; ngoặc lồng tối đa 5 (mục 2.7).
/// </summary>
public sealed class SearchQueryParser
{
    public const int MaxDepth = 5;

    private readonly List<Token> _tokens = new();
    private int _pos;
    private int _depth;

    private enum TokenKind { Word, Quoted, Qualifier, Neg, LParen, RParen, And, Or }
    private sealed record Token(TokenKind Kind, string Text, string? Value = null);

    public static SearchNode Parse(string query)
    {
        var parser = new SearchQueryParser();
        parser.Tokenize(query ?? string.Empty);
        if (parser._tokens.Count == 0) return new AndNode(Array.Empty<SearchNode>());
        var node = parser.ParseOr();
        if (parser._pos < parser._tokens.Count)
        {
            throw AppException.BadRequest($"Cú pháp tìm kiếm không hợp lệ gần '{parser._tokens[parser._pos].Text}'.");
        }
        return node;
    }

    // ---------------- tokenizer ----------------

    private void Tokenize(string input)
    {
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { _tokens.Add(new Token(TokenKind.LParen, "(")); i++; continue; }
            if (c == ')') { _tokens.Add(new Token(TokenKind.RParen, ")")); i++; continue; }
            if (c == '-' && i + 1 < input.Length && !char.IsWhiteSpace(input[i + 1])) { _tokens.Add(new Token(TokenKind.Neg, "-")); i++; continue; }
            if (c == '"')
            {
                var (text, next) = ReadQuoted(input, i);
                _tokens.Add(new Token(TokenKind.Quoted, text));
                i = next;
                continue;
            }

            // word hoặc qualifier
            var start = i;
            while (i < input.Length && !char.IsWhiteSpace(input[i]) && input[i] != '(' && input[i] != ')')
            {
                if (input[i] == ':' && i > start)
                {
                    // key:value — value có thể là "quoted" hoặc tới khoảng trắng
                    var key = input[start..i].ToLowerInvariant();
                    i++;
                    string value;
                    if (i < input.Length && input[i] == '"')
                    {
                        (value, i) = ReadQuoted(input, i);
                    }
                    else
                    {
                        var vs = i;
                        while (i < input.Length && !char.IsWhiteSpace(input[i]) && input[i] != ')') i++;
                        value = input[vs..i];
                    }
                    _tokens.Add(new Token(TokenKind.Qualifier, key + ":" + value, value) { });
                    _tokens[^1] = new Token(TokenKind.Qualifier, key, value);
                    goto next;
                }
                i++;
            }
            var word = input[start..i];
            if (word == "AND") _tokens.Add(new Token(TokenKind.And, word));
            else if (word == "OR") _tokens.Add(new Token(TokenKind.Or, word));
            else if (word.Length > 0) _tokens.Add(new Token(TokenKind.Word, word));
        next:;
        }
    }

    private static (string Text, int Next) ReadQuoted(string input, int i)
    {
        var sb = new StringBuilder();
        i++; // bỏ dấu "
        while (i < input.Length && input[i] != '"')
        {
            if (input[i] == '\\' && i + 1 < input.Length) { sb.Append(input[i + 1]); i += 2; continue; }
            sb.Append(input[i++]);
        }
        return (sb.ToString(), Math.Min(i + 1, input.Length));
    }

    // ---------------- parser ----------------

    private SearchNode ParseOr()
    {
        var items = new List<SearchNode> { ParseAnd() };
        while (Peek(TokenKind.Or))
        {
            _pos++;
            items.Add(ParseAnd());
        }
        return items.Count == 1 ? items[0] : new OrNode(items);
    }

    private SearchNode ParseAnd()
    {
        var items = new List<SearchNode>();
        while (_pos < _tokens.Count && !Peek(TokenKind.Or) && !Peek(TokenKind.RParen))
        {
            if (Peek(TokenKind.And)) { _pos++; continue; }
            items.Add(ParseUnary());
        }
        if (items.Count == 0) throw AppException.BadRequest("Biểu thức tìm kiếm rỗng.");
        return items.Count == 1 ? items[0] : new AndNode(items);
    }

    private SearchNode ParseUnary()
    {
        var t = _tokens[_pos];
        switch (t.Kind)
        {
            case TokenKind.Neg:
                _pos++;
                if (_pos >= _tokens.Count) throw AppException.BadRequest("Thiếu biểu thức sau '-'.");
                return new NotNode(ParseUnary());
            case TokenKind.LParen:
                if (++_depth > MaxDepth) throw AppException.BadRequest($"Ngoặc lồng quá {MaxDepth} cấp.");
                _pos++;
                var inner = ParseOr();
                if (!Peek(TokenKind.RParen)) throw AppException.BadRequest("Thiếu dấu ')'.");
                _pos++;
                _depth--;
                return inner;
            case TokenKind.Qualifier:
                _pos++;
                return new QualifierNode(t.Text, t.Value ?? string.Empty);
            case TokenKind.Word:
            case TokenKind.Quoted:
                _pos++;
                return new TextNode(t.Text);
            case TokenKind.RParen:
                throw AppException.BadRequest("Dấu ')' thừa.");
            default:
                throw AppException.BadRequest($"Token không hợp lệ '{t.Text}'.");
        }
    }

    private bool Peek(TokenKind kind) => _pos < _tokens.Count && _tokens[_pos].Kind == kind;

    // ---------------- tiện ích ----------------

    /// <summary>Lấy các qualifier "meta" (sort, in) ở mức ngoài cùng và trả về cây còn lại.</summary>
    public static (SearchNode Node, Dictionary<string, string> Meta) ExtractMeta(SearchNode node, params string[] keys)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        SearchNode Walk(SearchNode n)
        {
            switch (n)
            {
                case QualifierNode q when set.Contains(q.Key):
                    meta[q.Key] = q.Value;
                    return new AndNode(Array.Empty<SearchNode>());
                case AndNode a:
                    return new AndNode(a.Items.Select(Walk).Where(x => x is not AndNode { Items.Count: 0 }).ToList());
                default:
                    return n;
            }
        }
        return (Walk(node), meta);
    }
}
