using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using Microsoft.Extensions.Caching.Memory;

namespace IncidentTracker.Api.Tests.Unit;

/// <summary>modify_02 — BR-SEC-02 (sanitize khi render), BR-SOCIAL-04 (@mention), tham chiếu #N, closing keyword.</summary>
public sealed class MarkdownRendererTests
{
    private static MarkdownRenderer NewRenderer() => new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void Script_and_event_handlers_are_stripped()
    {
        var r = NewRenderer().Render("Hello <script>alert(1)</script> <img src=x onerror=alert(2)> **bold**", "support");

        Assert.DoesNotContain("<script", r.Html);
        Assert.DoesNotContain("onerror", r.Html);
        Assert.Contains("<strong>bold</strong>", r.Html);
    }

    [Fact]
    public void Javascript_urls_are_removed_and_external_links_get_rel_nofollow()
    {
        var r = NewRenderer().Render("[x](javascript:alert(1)) [gh](https://github.com)", "support");

        Assert.DoesNotContain("javascript:", r.Html);
        Assert.Contains("href=\"https://github.com\"", r.Html);
        Assert.Contains("nofollow", r.Html);
    }

    [Fact]
    public void Mentions_and_issue_refs_become_links_but_not_inside_code()
    {
        var r = NewRenderer().Render("cc @kien xem #12 và other#7, `@no #99` \n\n```\n@nope #100\n```", "support",
            new HashSet<string> { "kien" });

        Assert.Contains("class=\"user-mention\" href=\"/profiles/kien\"", r.Html);
        Assert.Contains("href=\"/projects/support/issues/12\"", r.Html);
        Assert.Contains("href=\"/projects/other/issues/7\"", r.Html);
        Assert.DoesNotContain("/users/no\"", r.Html);
        Assert.DoesNotContain("issues/99", r.Html);
        Assert.DoesNotContain("issues/100", r.Html);
        Assert.Equal(new[] { "kien" }, r.Mentions.ToArray());
        Assert.Contains(r.References, x => x.Number == 12 && x.ProjectSlug is null);
        Assert.Contains(r.References, x => x.Number == 7 && x.ProjectSlug == "other");
    }

    [Fact]
    public void Unknown_login_is_not_linked_when_known_set_provided()
    {
        var r = NewRenderer().Render("hi @ghost", "support", new HashSet<string> { "kien" });
        Assert.DoesNotContain("user-mention", r.Html);
        Assert.Empty(r.Mentions);
    }

    [Fact]
    public void Task_list_renders_disabled_checkboxes()
    {
        var r = NewRenderer().Render("- [x] done\n- [ ] todo", "support");
        Assert.Contains("type=\"checkbox\"", r.Html);
        Assert.Contains("disabled", r.Html);
    }

    [Fact]
    public void Extractors_skip_code_and_find_closing_keywords()
    {
        var mentions = MarkdownRenderer.ExtractMentions("@a `@b` ```\n@c\n``` @d-e");
        Assert.Equal(new[] { "a", "d-e" }, mentions.OrderBy(x => x).ToArray());

        var closing = MarkdownRenderer.ExtractClosingReferences("Fixes #12, resolves support#3 and mentions #99");
        Assert.Equal(2, closing.Count);
        Assert.Contains(closing, c => c.Number == 12);
        Assert.Contains(closing, c => c.Number == 3 && c.ProjectSlug == "support");
    }

    [Fact]
    public void Enum_naming_round_trips_upper_snake()
    {
        Assert.Equal("NOT_PLANNED", EnumNaming.Format(StateReason.NotPlanned));
        Assert.Equal("TOO_HEATED", EnumNaming.Format(LockReason.TooHeated));
        Assert.Equal("THUMBS_UP", EnumNaming.Format(ReactionType.ThumbsUp));
        Assert.Equal(StateReason.NotPlanned, EnumNaming.Parse<StateReason>("not_planned"));
        Assert.Equal(StateReason.NotPlanned, EnumNaming.Parse<StateReason>("not-planned"));
        Assert.Equal("P0", EnumNaming.Format(TicketPriority.P0));
        Assert.Equal(TicketPriority.P2, EnumNaming.Parse<TicketPriority>("p2"));
    }

    [Fact]
    public void Cursor_round_trips_and_clamps_page_size()
    {
        var c = Cursor.Encode(123456789L);
        Assert.Equal(123456789L, Cursor.DecodeSequence(c));
        Assert.Null(Cursor.DecodeSequence(null));
        Assert.Equal(30, Cursor.ClampPageSize(null));
        Assert.Equal(100, Cursor.ClampPageSize(500));
        Assert.Throws<AppException>(() => Cursor.DecodeSequence("###"));
    }
}
