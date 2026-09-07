using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// modify_02 — BR-REL-01: cùng Idempotency-Key + cùng body → replay response gốc (không tạo bản ghi
/// thứ hai); cùng key khác body → 422; key sai định dạng → 400. Dùng endpoint ghi có sẵn của R1
/// vì middleware áp dụng cho mọi request ghi đã xác thực.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class IdempotencyTests
{
    private readonly ApiFixture _fx;

    public IdempotencyTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Same_key_same_body_replays_original_response()
    {
        var support = await _fx.SupportAsync();
        var key = Guid.NewGuid().ToString();
        var body = new { title = "Idempotent incident " + key[..8], severity = "Low" };

        var first = await Send(support, key, body);
        var second = await Send(support, key, body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));

        var id1 = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
        var id2 = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task Same_key_different_body_is_rejected_with_422()
    {
        var support = await _fx.SupportAsync();
        var key = Guid.NewGuid().ToString();

        var first = await Send(support, key, new { title = "Idempotent A " + key[..8], severity = "Low" });
        var second = await Send(support, key, new { title = "Idempotent B " + key[..8], severity = "Low" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Malformed_key_is_rejected_with_400()
    {
        var support = await _fx.SupportAsync();
        var response = await Send(support, "bad key with spaces!", new { title = "Idempotent C", severity = "Low" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects/support/incidents")
        {
            Content = JsonContent.Create(body, options: ApiFactory.Json)
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }
}
