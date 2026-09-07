using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using static IncidentTracker.Api.Tests.Integration.TicketApi;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// GOAL-SEC-01 / US-EV-03 AC01–AC02 (Architecture v3.1): qua SignalR, nhân viên nhận INTERNAL_NOTE;
/// khách hàng đang mở cùng ticket không nhận byte nào của ghi chú nội bộ nhưng vẫn nhận COMMENTED.
/// Đường đi thật: API → Event Store → Outbox → MassTransit → TicketEventBroadcaster → Hub.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RealtimeTests
{
    private readonly ApiFixture _fx;

    public RealtimeTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Internal_note_reaches_staff_socket_but_never_customer_socket()
    {
        var support = await _fx.SupportAsync();
        var customer = await _fx.CustomerAsync();
        var t = await CreateTicketAsync(customer, "realtime");
        var n = I(t, "number");
        var ticketId = Guid.Parse(S(t, "id"));

        await using var staffHub = await ConnectAsync(ApiFixture.SupportEmail, ApiFixture.Password);
        await using var customerHub = await ConnectAsync(ApiFixture.CustomerEmail, ApiFixture.Password);

        var staffEvents = new List<JsonElement>();
        var customerEvents = new List<JsonElement>();
        staffHub.On<JsonElement>("ReceiveEvent", e => { lock (staffEvents) staffEvents.Add(e); });
        customerHub.On<JsonElement>("ReceiveEvent", e => { lock (customerEvents) customerEvents.Add(e); });

        await staffHub.InvokeAsync("JoinTicket", ticketId);
        await customerHub.InvokeAsync("JoinTicket", ticketId);

        var note = await support.SendAsync(Post($"/api/projects/support/tickets/{n}/internal-notes", new { body = "chỉ nội bộ" }));
        note.EnsureSuccessStatusCode();
        await CommentAsync(support, n, "trả lời công khai");

        // Outbox delivery poll 1s + consumer; chờ tối đa 15s cho tới khi khách nhận COMMENTED.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (customerEvents)
            {
                if (customerEvents.Any(e => e.GetProperty("eventType").GetString() == "COMMENTED")) break;
            }
            await Task.Delay(250);
        }
        await Task.Delay(1500); // thêm thời gian để nếu INTERNAL_NOTE bị rò thì nó cũng đã tới.

        lock (staffEvents)
        {
            Assert.Contains(staffEvents, e => e.GetProperty("eventType").GetString() == "INTERNAL_NOTE");
            Assert.Contains(staffEvents, e => e.GetProperty("eventType").GetString() == "COMMENTED");
            var internalNote = staffEvents.First(e => e.GetProperty("eventType").GetString() == "INTERNAL_NOTE");
            Assert.Contains("chỉ nội bộ", internalNote.GetProperty("bodyHtml").GetString());
        }
        lock (customerEvents)
        {
            Assert.Contains(customerEvents, e => e.GetProperty("eventType").GetString() == "COMMENTED");
            Assert.DoesNotContain(customerEvents, e => e.GetProperty("eventType").GetString() == "INTERNAL_NOTE");
            Assert.DoesNotContain(customerEvents, e => (e.GetProperty("bodyHtml").GetString() ?? string.Empty).Contains("chỉ nội bộ"));
        }
    }

    /// <summary>
    /// Event im lặng không được đẩy thành dòng timeline.
    ///
    /// Truy vấn timeline lọc chúng ra, nên nếu kênh real-time vẫn phát thì người dùng thấy dòng
    /// "… reacted" hiện lên rồi **biến mất** ở lần tải kế tiếp — hai nguồn sự thật cho cùng một
    /// câu hỏi "cái gì là một dòng". Tín hiệu `TicketChanged` thì vẫn phải tới: đó mới là thứ báo
    /// cho người đang mở ticket biết có gì đó vừa đổi.
    /// </summary>
    [Fact]
    public async Task Tha_emoji_khong_dung_len_mot_dong_timeline()
    {
        var support = await _fx.SupportAsync();
        var t = await CreateTicketAsync(support, "realtime-reaction");
        var n = I(t, "number");
        var ticketId = Guid.Parse(S(t, "id"));

        await using var hub = await ConnectAsync(ApiFixture.SupportEmail, ApiFixture.Password);
        var rows = new List<JsonElement>();
        var changes = new List<JsonElement>();
        hub.On<JsonElement>("ReceiveEvent", e => { lock (rows) rows.Add(e); });
        hub.On<JsonElement>("TicketChanged", e => { lock (changes) changes.Add(e); });
        await hub.InvokeAsync("JoinTicket", ticketId);

        (await support.SendAsync(Post($"/api/projects/support/tickets/{n}/reactions/toggle",
            new { content = "+1" }))).EnsureSuccessStatusCode();

        // Chờ tín hiệu nhẹ tới rồi mới kết luận — nếu chưa có gì tới thì phép khẳng định bên dưới
        // sẽ xanh vì lý do sai (chưa kịp phát), chứ không phải vì đã lọc đúng.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (changes)
            {
                if (changes.Any(e => e.GetProperty("eventType").GetString() == "REACTED")) break;
            }
            await Task.Delay(250);
        }

        lock (changes)
        {
            Assert.Contains(changes, e => e.GetProperty("eventType").GetString() == "REACTED");
        }
        lock (rows)
        {
            Assert.DoesNotContain(rows, e => e.GetProperty("eventType").GetString() == "REACTED");
        }

        // Và timeline qua REST cũng không có dòng nào — hai bên nói cùng một câu.
        var timeline = await TimelineAsync(support, n);
        Assert.DoesNotContain(timeline, e => S(e, "eventType") == "REACTED");
    }

    private async Task<HubConnection> ConnectAsync(string email, string password)
    {
        var token = await _fx.Factory.LoginTokenAsync(email, password);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_fx.Factory.Server.BaseAddress, $"ticket-hub?access_token={token}"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _fx.Factory.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }
}
