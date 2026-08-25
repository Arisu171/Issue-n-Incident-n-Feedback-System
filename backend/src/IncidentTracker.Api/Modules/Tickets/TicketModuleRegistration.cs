namespace IncidentTracker.Api.Modules.Tickets;

/// <summary>
/// Đăng ký DI cho toàn bộ module Tickets. Các bước modify_03..08 thêm service vào đây để
/// Program.cs chỉ có một dòng <c>AddTicketModules()</c>.
/// </summary>
public static class TicketModuleRegistration
{
    public static IServiceCollection AddTicketModules(this IServiceCollection services)
    {
        services.AddScoped<TicketQueries>();
        services.AddScoped<TicketService>();
        services.AddScoped<TimelineService>();
        services.AddScoped<CommentService>();
        services.AddSingleton<TicketExtensions>();

        // modify_04 — tổ chức
        services.AddScoped<Organization.ProjectService>();
        services.AddScoped<Organization.ProjectMemberService>();
        services.AddScoped<Organization.ProjectCatalogService>();
        services.AddScoped<Organization.LabelService>();
        services.AddScoped<Organization.MilestoneService>();
        services.AddScoped<Organization.IssueTypeService>();
        services.AddScoped<Organization.TemplateService>();
        services.AddScoped<Organization.BoardService>();

        // modify_05 — quan hệ
        services.AddScoped<Relations.RelationService>();
        services.AddScoped<Relations.VcsIntegrationService>();

        // modify_06 — tương tác
        services.AddScoped<Social.ReactionService>();
        services.AddScoped<Social.SubscriptionService>();
        services.AddScoped<Social.NotificationService>();
        services.AddSingleton<Social.IEmailSender, Social.LoggingEmailSender>();

        // modify_07 — tìm kiếm
        services.AddScoped<Search.SearchService>();

        // modify_08 — tích hợp & vận hành
        services.AddScoped<Webhooks.WebhookService>();
        services.AddScoped<Webhooks.WebhookDispatcher>();
        services.AddHttpClient("webhooks", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("UnifiedTicketing-Hookshot/1.0");
        });
        services.AddSingleton<Webhooks.IWebhookHttpClient, Webhooks.HttpWebhookClient>();
        services.AddHostedService<Webhooks.WebhookRetryJob>();
        services.AddHostedService<Sla.TicketSlaScheduler>();
        services.AddSingleton<Storage.IVirusScanner, Storage.NoOpVirusScanner>();
        services.AddSingleton<Storage.LocalStorageProvider>();
        services.AddSingleton<Storage.IStorageProvider>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Infrastructure.TicketingOptions>>().Value;
            return options.Storage.S3.Enabled
                ? new Storage.S3StorageProvider(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Infrastructure.TicketingOptions>>(), sp.GetRequiredService<TimeProvider>())
                : sp.GetRequiredService<Storage.LocalStorageProvider>();
        });
        return services;
    }
}
