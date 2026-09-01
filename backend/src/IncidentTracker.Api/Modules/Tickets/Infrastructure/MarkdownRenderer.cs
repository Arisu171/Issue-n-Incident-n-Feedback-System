using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using Markdig;
using Microsoft.Extensions.Caching.Memory;

namespace IncidentTracker.Api.Modules.Tickets.Infrastructure;

/// <summary>Kết quả render một đoạn Markdown.</summary>
public sealed record RenderedMarkdown(string Html, IReadOnlySet<string> Mentions, IReadOnlyList<TicketRef> References);

/// <summary><c>#N</c> hoặc <c>project#N</c> tìm thấy trong nội dung.</summary>
public sealed record TicketRef(string? ProjectSlug, int Number);

/// <summary>
/// BR-SEC-02 (Architecture v3.1): Markdown được lưu thô; HTML chỉ sinh lúc render và đi qua
/// sanitizer allowlist trước khi rời server. Client render <c>body_html</c> qua DOMPurify lần nữa.
///
/// <para>Pipeline: Markdig (GFM: table, task list, autolink, strikethrough, emoji; xuống dòng mềm
/// thành <c>&lt;br&gt;</c> như ô comment của GitHub) → HtmlSanitizer → AngleSharp đi qua từng text
/// node (bỏ <c>a/code/pre</c>) để biến <c>@login</c> và <c>#N</c> thành link.</para>
/// </summary>
public sealed partial class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .UseEmojiAndSmiley()
        .UseAutoLinks()
        .Build();

    private readonly HtmlSanitizer _sanitizer;
    private readonly IMemoryCache _cache;
    private readonly HtmlParser _parser = new();

    public MarkdownRenderer(IMemoryCache cache)
    {
        _cache = cache;
        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedTags.UnionWith(new[] { "input", "details", "summary", "del", "ins", "kbd", "sup", "sub", "mark" });
        _sanitizer.AllowedAttributes.UnionWith(new[] { "class", "type", "checked", "disabled", "align", "id", "start" });
        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.AllowedSchemes.UnionWith(new[] { "http", "https", "mailto" });
        _sanitizer.AllowedCssProperties.Clear();
        _sanitizer.RemovingAttribute += (_, e) =>
        {
            // Chỉ giữ class do Markdig sinh (task-list-item, language-xxx...) — chặn class lạ.
            if (e.Attribute.Name == "class" && (e.Attribute.Value.StartsWith("task-list") || e.Attribute.Value.StartsWith("language-")))
            {
                e.Cancel = true;
            }
        };
        _sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is IHtmlAnchorElement a)
            {
                a.SetAttribute("rel", "nofollow noopener noreferrer");
                if (a.Href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    a.SetAttribute("target", "_blank");
                }
            }
            else if (e.Node is IHtmlInputElement input)
            {
                // Task list: checkbox chỉ hiển thị, không tương tác trực tiếp.
                input.SetAttribute("disabled", "disabled");
            }
        };
    }

    /// <summary>
    /// Render + sanitize + link mention/ref. <paramref name="knownLogins"/> = null nghĩa là link
    /// mọi <c>@login</c> hợp lệ; truyền tập login tồn tại để chỉ link người thật (giống GitHub).
    /// </summary>
    public RenderedMarkdown Render(string? markdown, string projectSlug, IReadOnlySet<string>? knownLogins = null)
    {
        markdown ??= string.Empty;
        var key = "md:" + projectSlug + ":" + (knownLogins is null ? "*" : string.Join(",", knownLogins.OrderBy(x => x))) + ":" + Sha(markdown);
        if (_cache.TryGetValue<RenderedMarkdown>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var rawHtml = Markdown.ToHtml(markdown, Pipeline);
        var safeHtml = _sanitizer.Sanitize(rawHtml);

        var doc = _parser.ParseDocument("<body>" + safeHtml + "</body>");
        var mentions = new HashSet<string>(StringComparer.Ordinal);
        var refs = new List<TicketRef>();
        LinkifyTextNodes(doc.Body!, projectSlug, knownLogins, mentions, refs, doc);

        var result = new RenderedMarkdown(doc.Body!.InnerHtml, mentions, refs);
        _cache.Set(key, result, TimeSpan.FromMinutes(30));
        return result;
    }

    /// <summary>Băm thân Markdown (dùng cho <c>previous_body_hash</c> của event sửa).</summary>
    public static string Sha(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // ------------------------------------------------------------------
    // Trích xuất từ Markdown thô (cho Regex Worker) — bỏ qua code span/fence.
    // ------------------------------------------------------------------

    public static IReadOnlySet<string> ExtractMentions(string? markdown)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in MentionRegex().Matches(StripCode(markdown)))
        {
            set.Add(m.Groups["login"].Value.ToLowerInvariant());
        }
        return set;
    }

    public static IReadOnlyList<TicketRef> ExtractReferences(string? markdown)
    {
        var list = new List<TicketRef>();
        foreach (Match m in RefRegex().Matches(StripCode(markdown)))
        {
            var slug = m.Groups["slug"].Success ? m.Groups["slug"].Value.ToLowerInvariant() : null;
            list.Add(new TicketRef(slug, int.Parse(m.Groups["num"].Value)));
        }
        return list;
    }

    /// <summary>BR-REL-06: closing keyword → danh sách ticket sẽ đóng.</summary>
    public static IReadOnlyList<TicketRef> ExtractClosingReferences(string? text)
    {
        var list = new List<TicketRef>();
        foreach (Match m in ClosingRegex().Matches(StripCode(text)))
        {
            var slug = m.Groups["slug"].Success ? m.Groups["slug"].Value.ToLowerInvariant() : null;
            list.Add(new TicketRef(slug, int.Parse(m.Groups["num"].Value)));
        }
        return list;
    }

    private static string StripCode(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }
        var noFence = FenceRegex().Replace(markdown, " ");
        return InlineCodeRegex().Replace(noFence, " ");
    }

    private void LinkifyTextNodes(INode node, string projectSlug, IReadOnlySet<string>? knownLogins,
        HashSet<string> mentions, List<TicketRef> refs, IHtmlDocument doc)
    {
        foreach (var child in node.ChildNodes.ToList())
        {
            if (child is IElement el)
            {
                var tag = el.TagName.ToLowerInvariant();
                if (tag is "a" or "code" or "pre" or "script" or "style")
                {
                    continue;
                }
                LinkifyTextNodes(el, projectSlug, knownLogins, mentions, refs, doc);
            }
            else if (child is IText text)
            {
                var value = text.Data;
                if (!value.Contains('@') && !value.Contains('#'))
                {
                    continue;
                }

                var fragment = doc.CreateDocumentFragment();
                var last = 0;
                var any = false;
                foreach (Match m in TokenRegex().Matches(value))
                {
                    IElement? link = null;
                    if (m.Groups["login"].Success)
                    {
                        var login = m.Groups["login"].Value.ToLowerInvariant();
                        if (knownLogins is null || knownLogins.Contains(login))
                        {
                            mentions.Add(login);
                            link = doc.CreateElement("a");
                            link.ClassName = "user-mention";
                            // Trang hồ sơ nằm ở `/profiles/{login}`; `/users` chỉ là tiền tố của
                            // API quản trị tài khoản, không có màn hình nào. Nhắc tên trỏ vào đó
                            // là mỗi lần bấm một cú 404.
                            link.SetAttribute("href", "/profiles/" + login);
                            link.TextContent = "@" + login;
                        }
                    }
                    else if (m.Groups["num"].Success)
                    {
                        var slug = m.Groups["slug"].Success ? m.Groups["slug"].Value.ToLowerInvariant() : null;
                        var number = int.Parse(m.Groups["num"].Value);
                        refs.Add(new TicketRef(slug, number));
                        link = doc.CreateElement("a");
                        link.ClassName = "issue-link";
                        link.SetAttribute("href", $"/projects/{slug ?? projectSlug}/issues/{number}");
                        link.TextContent = m.Value.TrimStart();
                    }

                    if (link is null)
                    {
                        continue;
                    }

                    any = true;
                    var prefixLength = m.Groups["pre"].Length;
                    fragment.AppendChild(doc.CreateTextNode(value[last..(m.Index + prefixLength)]));
                    fragment.AppendChild(link);
                    last = m.Index + m.Length;
                }

                if (any)
                {
                    fragment.AppendChild(doc.CreateTextNode(value[last..]));
                    node.ReplaceChild(fragment, text);
                }
            }
        }
    }

    // @login (không đứng sau chữ/số) — quy tắc login của GitHub.
    [GeneratedRegex(@"(?<![\w@])@(?<login>[a-zA-Z0-9](?:[a-zA-Z0-9]|-(?=[a-zA-Z0-9])){0,38})")]
    private static partial Regex MentionRegex();

    // #N hoặc slug#N (không đứng sau chữ/số/&).
    [GeneratedRegex(@"(?<![\w&/#])(?:(?<slug>[a-zA-Z0-9][a-zA-Z0-9._-]{0,98}))?#(?<num>\d{1,9})\b")]
    private static partial Regex RefRegex();

    // Token gộp dùng khi đi qua text node đã render.
    [GeneratedRegex(@"(?<pre>^|[^\w@&/#])(?:@(?<login>[a-zA-Z0-9](?:[a-zA-Z0-9]|-(?=[a-zA-Z0-9])){0,38})|(?<slug>[a-zA-Z0-9][a-zA-Z0-9._-]{0,98})?#(?<num>\d{1,9}))(?![\w-])")]
    private static partial Regex TokenRegex();

    // close/closes/closed/fix/fixes/fixed/resolve/resolves/resolved #N | slug#N
    [GeneratedRegex(@"\b(?:close|closes|closed|fix|fixes|fixed|resolve|resolves|resolved)\s*:?\s+(?:(?<slug>[a-zA-Z0-9][a-zA-Z0-9._-]{0,98}))?#(?<num>\d{1,9})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingRegex();

    [GeneratedRegex(@"```[\s\S]*?```|~~~[\s\S]*?~~~")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCodeRegex();
}
