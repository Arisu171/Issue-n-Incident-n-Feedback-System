using IncidentTracker.Api.Common;
using IncidentTracker.Api.Modules.Tickets.Search;

namespace IncidentTracker.Api.Tests.Unit;

/// <summary>modify_07 — ngữ pháp Query DSL (Architecture v3.1, mục 2.7).</summary>
public sealed class SearchQueryParserTests
{
    [Fact]
    public void Qualifiers_and_text_join_with_implicit_AND()
    {
        var node = SearchQueryParser.Parse("is:open label:bug,urgent \"lỗi đăng nhập\" assignee:@me");
        var and = Assert.IsType<AndNode>(node);
        Assert.Equal(4, and.Items.Count);
        Assert.Equal(new QualifierNode("is", "open"), and.Items[0]);
        Assert.Equal(new QualifierNode("label", "bug,urgent"), and.Items[1]);
        Assert.Equal(new TextNode("lỗi đăng nhập"), and.Items[2]);
        Assert.Equal(new QualifierNode("assignee", "@me"), and.Items[3]);
    }

    [Fact]
    public void Negation_OR_and_parentheses()
    {
        var node = SearchQueryParser.Parse("-label:wontfix (author:kien OR mentions:kien) state:open");
        var and = Assert.IsType<AndNode>(node);
        Assert.IsType<NotNode>(and.Items[0]);
        var or = Assert.IsType<OrNode>(and.Items[1]);
        Assert.Equal(2, or.Items.Count);
        Assert.Equal(new QualifierNode("state", "open"), and.Items[2]);
    }

    [Fact]
    public void Quoted_qualifier_values_and_dates()
    {
        var node = SearchQueryParser.Parse("label:\"good first issue\" created:>2026-01-01 comments:5..20 reason:\"not planned\"");
        var and = Assert.IsType<AndNode>(node);
        Assert.Equal(new QualifierNode("label", "good first issue"), and.Items[0]);
        Assert.Equal(new QualifierNode("created", ">2026-01-01"), and.Items[1]);
        Assert.Equal(new QualifierNode("comments", "5..20"), and.Items[2]);
        Assert.Equal(new QualifierNode("reason", "not planned"), and.Items[3]);
    }

    [Fact]
    public void Meta_qualifiers_sort_and_in_are_extracted()
    {
        var (rest, meta) = SearchQueryParser.ExtractMeta(SearchQueryParser.Parse("is:open sort:updated-desc in:title,comments crash"), "sort", "in");
        Assert.Equal("updated-desc", meta["sort"]);
        Assert.Equal("title,comments", meta["in"]);
        var and = Assert.IsType<AndNode>(rest);
        Assert.Equal(2, and.Items.Count);
    }

    [Fact]
    public void Too_deep_parentheses_and_unbalanced_are_rejected()
    {
        Assert.Throws<AppException>(() => SearchQueryParser.Parse("((((((a))))))"));
        Assert.Throws<AppException>(() => SearchQueryParser.Parse("(a OR b"));
        Assert.Throws<AppException>(() => SearchQueryParser.Parse("a) b"));
    }

    [Fact]
    public void Empty_query_is_match_all()
    {
        var node = SearchQueryParser.Parse("   ");
        var and = Assert.IsType<AndNode>(node);
        Assert.Empty(and.Items);
    }
}
