using System.Text;
using System.Threading.RateLimiting;
using HealthChecks.NpgSql;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Feedbacks;
using IncidentTracker.Api.Modules.Identity;
using IncidentTracker.Api.Modules.Incidents;
using IncidentTracker.Api.Modules.Rbac;
using IncidentTracker.Api.Modules.Tickets;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Modules.Tickets.Realtime;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using IncidentTracker.Api.Observability;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Gắn vào resource của OpenTelemetry để phân biệt các bản triển khai.
const string ServiceVersion = "1.0.0";

// ---------------------------------------------------------------------------
// Cấu hình
// ---------------------------------------------------------------------------
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.SectionName));
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

builder.Services.AddOptions<RegistrationOptions>()
    .Bind(builder.Configuration.GetSection(RegistrationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<SlaOptions>()
    .Bind(builder.Configuration.GetSection(SlaOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<FeedbackOptions>()
    .Bind(builder.Configuration.GetSection(FeedbackOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<IncidentTracker.Api.Modules.Revisions.EditClaimOptions>()
    .Bind(builder.Configuration.GetSection(IncidentTracker.Api.Modules.Revisions.EditClaimOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<TicketingOptions>()
    .Bind(builder.Configuration.GetSection(TicketingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "Thiếu ConnectionStrings:Default. Đặt CONNECTIONSTRINGS__DEFAULT trong .env.");

// ---------------------------------------------------------------------------
// Persistence — CMP-05
// ---------------------------------------------------------------------------
// Schema lấy ngay từ `Search Path` của chuỗi kết nối — một nguồn sự thật, không thêm biến
// môi trường thứ hai có thể lệch nhau.
//
// <b>Vì sao bắt buộc phải nói cho EF biết.</b> `Search Path` khiến DDL không ghi schema đi vào
// schema đó, nhưng bảng lịch sử migration thì EF tra bằng schema MẶC ĐỊNH của model — tức
// `public`. Không tìm thấy, EF phát lệnh CREATE TABLE không ghi schema, lệnh này lại rơi vào
// schema trong Search Path và đụng đúng bảng đang có: `42P07 relation already exists`.
// Lần khởi động ĐẦU chạy lọt vì lúc đó chưa có bảng nào; mọi lần sau đều chết.
var migrationsSchema = new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
    .SearchPath?.Split(',')[0].Trim();

builder.Services.AddDbContext<AppDbContext>(o =>
{
    o.UseNpgsql(connectionString, npg =>
    {
        npg.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null);
        if (!string.IsNullOrWhiteSpace(migrationsSchema))
        {
            npg.MigrationsHistoryTable("__EFMigrationsHistory", migrationsSchema);
        }
    });
    if (builder.Environment.IsDevelopment())
    {
        o.EnableDetailedErrors();
    }
});

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);
// Bộ nhớ đệm tùy chọn cho việc nạp quyền theo request (ADR-004); mặc định tắt.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<ITransactionFaultHook, NoOpTransactionFaultHook>();
builder.Services.AddScoped<IdentityService>();
builder.Services.AddScoped<RbacService>();
// Lịch sử sửa nội dung dùng chung cho Incident, Feedback và bình luận của cả hai.
builder.Services.AddScoped<IncidentTracker.Api.Modules.Revisions.ContentRevisionService>();
builder.Services.AddScoped<IncidentTracker.Api.Modules.Revisions.EditClaimService>();
builder.Services.AddScoped<IncidentService>();
builder.Services.AddScoped<IncidentCommentService>();
builder.Services.AddScoped<FeedbackService>();

// ---------------------------------------------------------------------------
// Module Tickets — Architecture v3.1 (Event Store, Markdown, Outbox, SignalR)
// ---------------------------------------------------------------------------
builder.Services.AddScoped<TicketEventStore>();
builder.Services.AddSingleton<MarkdownRenderer>();
builder.Services.AddHostedService<TicketMaintenanceJob>();
// Dữ liệu trình diễn chạy nền, sau khi cổng đã mở — không được chặn khởi động.
builder.Services.AddHostedService<IncidentTracker.Api.Persistence.DemoDataHostedService>();
builder.Services.AddTicketModules();

var ticketing = builder.Configuration.GetSection(TicketingOptions.SectionName).Get<TicketingOptions>() ?? new TicketingOptions();

builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();
    x.AddConsumers(typeof(Program).Assembly);

    // Bus outbox: Publish trong cùng SaveChanges với event (mục 6.2, BR-EV-04).
    x.AddEntityFrameworkOutbox<AppDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
        // Inbox đã khoá hàng bằng SELECT ... FOR UPDATE (UsePostgres); Serializable mặc định chỉ sinh
        // thêm lỗi 40001 khi các projection upsert cùng một hàng ticket song song.
        o.IsolationLevel = System.Data.IsolationLevel.ReadCommitted;
        o.QueryDelay = TimeSpan.FromSeconds(1);
        o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
    });

    x.AddConsumeObserver<IncidentTracker.Api.Observability.ConsumerFaultObserver>();

    x.AddConfigureEndpointsCallback((context, _, cfg) =>
    {
        // Trần đồng thời cho mỗi endpoint: mặc định của transport là số CPU, mà một event ticket fan-out
        // ra tất cả consumer nên trên máy nhiều nhân sẽ có nhiều lượt consume hơn số connection trong pool.
        // Mỗi lượt giữ một connection suốt transaction inbox/outbox → pool cạn, timeout, rồi retry làm lại
        // đúng khối công việc đó (bão quan sát được khi xả outbox sau lúc seed dữ liệu trình diễn).
        cfg.ConcurrentMessageLimit = ticketing.ConsumerConcurrency;
        // BR-EV-04: exponential backoff; hết retry thì message vào queue *_error (DLQ, mục 7).
        cfg.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(2)));
        cfg.UseEntityFrameworkOutbox<AppDbContext>(context);
    });

    if (ticketing.RabbitMq.Enabled)
    {
        x.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(ticketing.RabbitMq.Host, ticketing.RabbitMq.VirtualHost, h =>
            {
                h.Username(ticketing.RabbitMq.Username);
                h.Password(ticketing.RabbitMq.Password);
            });
            cfg.ConfigureEndpoints(context);
        });
    }
    else
    {
        // Sai lệch có chủ đích (modify_00 §2.5): môi trường dev không có RabbitMQ.
        x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
    }
});

// Hiện diện đếm theo connection nên phải là singleton: mỗi scope một bảng riêng thì mọi
// người luôn hiện offline.
builder.Services.AddSingleton<IncidentTracker.Api.Modules.Identity.PresenceTracker>();
builder.Services.AddSignalR(o => o.AddFilter<HubRateLimitFilter>())
    .AddJsonProtocol(o =>
    {
        o.PayloadSerializerOptions.Converters.Add(new UpperSnakeEnumConverterFactory());
        o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// ---------------------------------------------------------------------------
// Observability — mục 6.9
// ---------------------------------------------------------------------------
builder.Services.AddAppObservability(builder.Configuration, ServiceVersion);

// Job nền cảnh báo sự cố vượt ngưỡng SLA.
builder.Services.AddHostedService<SlaMonitor>();

// ---------------------------------------------------------------------------
// Authentication — NFR-SEC-01: kiểm chữ ký, issuer, audience và expiry
// ---------------------------------------------------------------------------
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Giữ nguyên tên claim gốc (sub, perm) thay vì map sang URI dài của WS-Federation.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = AppClaimTypes.DisplayName,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };

        // 401/403 từ pipeline không ném exception nên không đi qua ExceptionHandlingMiddleware.
        // Không xử lý ở đây thì chúng trả body rỗng, lệch với error contract ProblemDetails
        // của mục 6.6 và với chính 403 do action filter sinh ra.
        options.Events = new JwtBearerEvents
        {
            // Mục 6.6 (v3.1): trình duyệt không gửi được header Authorization khi mở WebSocket,
            // nên SignalR nhận JWT qua query string access_token — chỉ trên đường dẫn của hub.
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken)
                    && context.HttpContext.Request.Path.StartsWithSegments(TicketHub.Path))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.HttpContext.RequestServices.GetRequiredService<AppMetrics>()
                    .AuthorizationDenied(StatusCodes.Status401Unauthorized);
                await ProblemDetailsWriter.WriteUnauthorizedAsync(context.HttpContext);
            },
            OnForbidden = context =>
            {
                var required = context.HttpContext.Items
                    .TryGetValue(PermissionAuthorizationHandler.MissingPermissionKey, out var value)
                    ? value as string
                    : null;
                context.HttpContext.RequestServices.GetRequiredService<AppMetrics>()
                    .AuthorizationDenied(StatusCodes.Status403Forbidden);
                return ProblemDetailsWriter.WriteForbiddenAsync(context.HttpContext, required);
            }
        };
    });

// ---------------------------------------------------------------------------
// Authorization — CMP-03, DRV-01
// ---------------------------------------------------------------------------
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthorization(options =>
{
    // Mặc định là từ chối. Không có dòng này thì một action mới quên gắn
    // [RequirePermission] sẽ im lặng trở thành endpoint công khai — lỗi không ai
    // nhìn thấy cho tới khi có người khai thác. Endpoint thật sự công khai
    // (login, register, health, metrics) phải nói ra bằng [AllowAnonymous].
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

// Authorization theo bản ghi (resource-based) — chống BOLA, xem ResourceAuthorization.cs.
builder.Services.AddSingleton<IAuthorizationHandler, IncidentAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, FeedbackAuthorizationHandler>();

// ---------------------------------------------------------------------------
// Rate limit đăng nhập (mục 6.6)
// ---------------------------------------------------------------------------
var rateLimits = builder.Configuration.GetSection(RateLimitOptions.SectionName)
    .Get<RateLimitOptions>() ?? new RateLimitOptions();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // BR-SEC-03 (v3.1): 429 luôn kèm Retry-After và body problem+json.
    options.OnRejected = async (context, ct) =>
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra)
            ? (int)Math.Ceiling(ra.TotalSeconds) : 60;
        context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString();
        await ProblemDetailsWriter.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests,
            "Quá nhiều yêu cầu", $"Vượt giới hạn tần suất. Thử lại sau {retryAfter} giây.",
            new Dictionary<string, object?> { ["retryAfterSeconds"] = retryAfter });
    };

    // BR-SEC-03: giới hạn đọc/ghi theo user (fallback IP) cho module Tickets.
    static string UserOrIp(HttpContext context)
        => context.User.FindFirst(AppClaimTypes.Subject)?.Value
           ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    options.AddPolicy(RateLimitPolicies.TicketRead, context =>
        RateLimitPartition.GetSlidingWindowLimiter(UserOrIp(context), _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = ticketing.ReadPerMinute,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0
        }));

    options.AddPolicy(RateLimitPolicies.TicketWrite, context =>
        RateLimitPartition.GetSlidingWindowLimiter(UserOrIp(context), _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = ticketing.WritePerMinute,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0
        }));
    options.AddPolicy(RateLimitPolicies.Login, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.LoginPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Cửa sổ một giờ: chặn kịch bản tạo hàng loạt tài khoản rác qua đường công khai.
    options.AddPolicy(RateLimitPolicies.Register, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimits.RegisterPerHour,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));
});

// ---------------------------------------------------------------------------
// MVC + error contract
// ---------------------------------------------------------------------------
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        // Enum module Tickets → UPPER_SNAKE (bản vẽ 5.2–5.4); phải đứng trước converter chung của R1.
        o.JsonSerializerOptions.Converters.Add(new UpperSnakeEnumConverterFactory());
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// Validation lỗi cũng đi theo ProblemDetails và mang correlation id (mục 6.6).
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Dữ liệu không hợp lệ",
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["correlationId"] = CorrelationIdMiddleware.Current(context.HttpContext);
        return new BadRequestObjectResult(problem) { ContentTypes = { ProblemDetailsWriter.ContentType } };
    };
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Incident & Feedback Tracker API",
        Version = "v1",
        Description = "RBAC + vòng đời sự cố ba trạng thái. Xem utils/docs/Architecture.md mục 6.6."
    });

    var scheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Dán access token nhận từ POST /api/auth/login.",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
    };
    c.AddSecurityDefinition("Bearer", scheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = Array.Empty<string>() });

    var xml = Path.Combine(AppContext.BaseDirectory,
        $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml))
    {
        c.IncludeXmlComments(xml);
    }
});

// ---------------------------------------------------------------------------
// Health — NFR-PORT-01: compose cần api báo healthy khi và chỉ khi DB kết nối được
// ---------------------------------------------------------------------------
builder.Services.Configure<IncidentTracker.Api.Common.HealthCheckOptions>(
    builder.Configuration.GetSection(IncidentTracker.Api.Common.HealthCheckOptions.SectionName));

var healthOptions = builder.Configuration.GetSection(IncidentTracker.Api.Common.HealthCheckOptions.SectionName)
    .Get<IncidentTracker.Api.Common.HealthCheckOptions>() ?? new IncidentTracker.Api.Common.HealthCheckOptions();

builder.Services.AddHealthChecks()
    // api tự nó: trả lời được request nghĩa là tiến trình còn sống.
    .AddCheck("api", () => HealthCheckResult.Healthy("API đang phục vụ request."),
        tags: new[] { HealthEndpoints.ReadyTag, HealthEndpoints.SystemTag })
    // db: có tag ready nên mất kết nối là container api chuyển sang unhealthy.
    .AddNpgSql(connectionString, name: "db",
        tags: new[] { HealthEndpoints.ReadyTag, HealthEndpoints.SystemTag })
    // web: CHỈ có tag system. Đưa vào ready sẽ tạo khóa chết vì compose khai báo
    // web depends_on api healthy — xem HealthEndpoints.
    .AddUrlGroup(new Uri(healthOptions.WebUrl), name: "web",
        timeout: TimeSpan.FromSeconds(healthOptions.TimeoutSeconds),
        tags: new[] { HealthEndpoints.SystemTag });

// Health check "web" KHÔNG được đi theo chuyển hướng.
//
// AddUrlGroup đăng ký một HttpClient tên "web", mà mặc định của HttpClient là bám redirect.
// Hậu quả đã gặp thật khi deploy: web nằm sau một cổng đăng nhập, /healthz trả 302 → 302 →
// 200 tại trang login của nhà cung cấp. Health check thấy 200 và báo XANH, trong khi không
// một người dùng nào mở nổi web. Một phép kiểm chấm điểm trang đăng nhập của bên thứ ba thì
// tệ hơn là không có, vì nó tạo niềm tin sai.
//
// Chặn redirect: 3xx rơi ra ngoài dải 200–299 nên check chuyển sang Unhealthy, đúng thực tế.
builder.Services.AddHttpClient("web")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// CORS cho frontend Next.js.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:3000" };
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()
    .WithExposedHeaders(CorrelationIdMiddleware.HeaderName, "ETag", "Location", "Link", "Retry-After",
        IdempotencyMiddleware.ReplayedHeader)));

var app = builder.Build();

// Điểm cắm module Tickets (Architecture v3.1): template renderer (UC-17) — các delegate còn lại
// (sub-issue, DSL, cảnh báo đóng) được gán ở modify_05/07.
app.Services.GetRequiredService<TicketExtensions>().TemplateRenderer = async (project, request, user, ct) =>
{
    await using var scope = app.Services.CreateAsyncScope();
    var templates = scope.ServiceProvider.GetRequiredService<IncidentTracker.Api.Modules.Tickets.Organization.TemplateService>();
    return await templates.RenderAsync(project, request, user, ct);
};
// UC-14: Query DSL cho GET /projects/{p}/tickets?q= (cùng scope request).
app.Services.GetRequiredService<TicketExtensions>().DslFilter = async (source, dsl, project, user) =>
{
    var scope = app.Services.GetRequiredService<IHttpContextAccessor>().HttpContext?.RequestServices
        ?? throw new InvalidOperationException("DslFilter cần HttpContext.");
    var search = scope.GetRequiredService<IncidentTracker.Api.Modules.Tickets.Search.SearchService>();
    return await search.ApplyAsync(source, dsl, user, CancellationToken.None);
};
// UC-10: tạo ticket trực tiếp làm sub-issue (chạy trong transaction tạo ticket của TicketService).
app.Services.GetRequiredService<TicketExtensions>().AttachParent = async (child, parentNumber, user, ct) =>
{
    var http = child.Project; // cùng scope DbContext với TicketService nhờ IHttpContextAccessor
    var scope = app.Services.GetRequiredService<IHttpContextAccessor>().HttpContext?.RequestServices
        ?? throw new InvalidOperationException("AttachParent cần HttpContext.");
    var relations = scope.GetRequiredService<IncidentTracker.Api.Modules.Tickets.Relations.RelationService>();
    var q = scope.GetRequiredService<TicketQueries>();
    IncidentTracker.Api.Modules.Tickets.TicketAccess.Ensure(IncidentTracker.Api.Modules.Tickets.TicketAccess.CanTriage(user), "Tạo sub-issue cần quyền Triage.");
    var parent = await q.LoadAsync(http.Slug, parentNumber, tracking: true, ct);
    await relations.AttachAsync(parent, child, user.GetUserId(), child.CreatedAt, ct);
};

// ---------------------------------------------------------------------------
// Pipeline — thứ tự theo Hình 7: correlation → lỗi → auth → authz → controller
// ---------------------------------------------------------------------------
// Mục 6.7 — "HTTPS ở production". Mặc định tắt vì compose của môn học chạy HTTP thuần và
// bật redirect sẽ làm healthcheck nội bộ nhận 307. Đặt SECURITY__REQUIREHTTPS=true khi
// triển khai sau một ingress có TLS.
var requireHttps = builder.Configuration.GetValue("Security:RequireHttps", false);
if (requireHttps)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Yêu cầu kỹ thuật "OpenAPI 2 endpoint" nói về việc đặc tả phải mô tả tối thiểu hai endpoint
// nghiệp vụ, không phải về việc phơi OpenAPI ra bao nhiêu URL. Đặc tả hiện mô tả toàn bộ
// operation của API (trên 30) nên yêu cầu đó đã đạt mà không cần phơi Swagger ra production.
//
// Mặc định vì vậy quay về an toàn: bật ở Development, tắt ở nơi khác. Vẫn cho phép bật tường
// minh bằng SWAGGER__ENABLED=true khi triển khai nội bộ hoặc để demo — đọc bằng TryParse nên
// giá trị rỗng hay sai định dạng đều rơi về mặc định theo môi trường thay vì làm sập app.
var swaggerEnabled = bool.TryParse(app.Configuration["Swagger:Enabled"], out var swaggerSetting)
    ? swaggerSetting
    : app.Environment.IsDevelopment();

if (swaggerEnabled)
{
    // Đặc tả máy đọc được tại /swagger/v1/swagger.json
    app.UseSwagger();

    // Giao diện thử API tại /swagger/index.html
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Incident Tracker API v1");
        c.DocumentTitle = "Incident & Feedback Tracker API";
    });
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();

// ADR-004 — token chỉ mang danh tính; vai trò và permission nạp từ database ở đây, sau khi đã
// biết "ai" và trước khi có ai hỏi "được làm gì".
app.UseMiddleware<PrincipalEnrichmentMiddleware>();

app.UseAuthorization();

// BR-REL-01 (v3.1): sau authorization để key gắn với user đã xác thực và endpoint đã được chọn.
app.UseMiddleware<IdempotencyMiddleware>();

app.MapControllers();

// UC-05 (v3.1): WebSocket real-time.
app.MapHub<TicketHub>(TicketHub.Path);

// FallbackPolicy áp lên CẢ request không khớp route nào: khi endpoint là null,
// AuthorizationPolicy.CombineAsync vẫn trả về fallback, nên một đường không tồn tại sẽ bị
// challenge thành 401 thay vì 404 — sai hợp đồng mã lỗi mục 3.3 và vô tình tiết lộ "đường
// này cần đăng nhập" cho người dò bề mặt API. Cho những request đó một endpoint tường minh:
// 404 ProblemDetails, công khai như mọi trang không tồn tại. Pattern "{*path}" thay cho mặc
// định "{*path:nonfile}" vì API không phục vụ file tĩnh — thiếu nó thì đường có đuôi file
// (vd /swagger/v1/swagger.json lúc Swagger tắt) vẫn lọt về nhánh endpoint-null và trả 401.
app.MapFallback("{*path}", context => ProblemDetailsWriter.WriteAsync(
    context,
    StatusCodes.Status404NotFound,
    "Không tìm thấy",
    "Đường dẫn không tồn tại. Xem đặc tả tại /swagger khi Swagger được bật.")).AllowAnonymous();

// API-Health — hai endpoint công khai, xem HealthEndpoints để biết vì sao phải tách:
//   GET /api/health         api + db      → healthcheck của docker compose
//   GET /api/health/system  api + db + web → một lời gọi từ Postman biết toàn cảnh
app.MapAppHealthChecks();

// Mục 7 (v3.1): probe K8s. live = tiến trình sống; ready = api + db + bus MassTransit.
static Task WriteHealthJson(HttpContext context, Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
{
    context.Response.ContentType = "application/json";
    return context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
    {
        status = report.Status.ToString(),
        totalDurationMs = (int)report.TotalDuration.TotalMilliseconds,
        entries = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), durationMs = (int)e.Value.Duration.TotalMilliseconds, description = e.Value.Description })
    }));
}
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Name == "api",
    ResponseWriter = WriteHealthJson
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains(HealthEndpoints.ReadyTag) || r.Name.StartsWith("masstransit", StringComparison.OrdinalIgnoreCase),
    ResponseWriter = WriteHealthJson
}).AllowAnonymous();

// Endpoint Prometheus scrape (mục 6.9). Không nằm sau authorization vì hệ thống giám sát
// thường chạy trong mạng nội bộ và không có JWT; ở production nên chặn bằng network policy.
if (builder.Configuration.GetSection(ObservabilityOptions.SectionName)
        .Get<ObservabilityOptions>()?.EnablePrometheus ?? true)
{
    // AllowAnonymous là bắt buộc từ khi có FallbackPolicy: Prometheus scrape không mang JWT.
    app.MapPrometheusScrapingEndpoint().AllowAnonymous();
}

// Cấu hình sai vai trò mặc định của đăng ký là lỗi im lặng nguy hiểm nhất ở đây, nên phải
// nói to lúc khởi động thay vì để nó âm thầm bị bỏ qua.
var registrationOptions = app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<RegistrationOptions>>().Value;
if (registrationOptions.Enabled)
{
    var rejected = registrationOptions.RejectedRoles();
    if (rejected.Count > 0)
    {
        app.Logger.LogWarning(
            "Registration:DefaultRoles có {Count} vai trò bị chặn vì không nằm trong danh sách "
            + "được phép tự cấp ({Allowed}): {Rejected}. Chúng đã bị bỏ qua.",
            rejected.Count, string.Join(", ", RegistrationOptions.AssignableRoles),
            string.Join(", ", rejected));
    }

    app.Logger.LogInformation(
        "Tự đăng ký đang BẬT. Vai trò mặc định: {Roles}. Tên miền cho phép: {Domains}. Chờ duyệt: {Approval}.",
        string.Join(", ", registrationOptions.SafeDefaultRoles()) is { Length: > 0 } r ? r : "(không có)",
        registrationOptions.AllowedDomainList().Count == 0
            ? "(mọi tên miền)"
            : string.Join(", ", registrationOptions.AllowedDomainList()),
        registrationOptions.RequireApproval);
}

// Bất biến giữa số consumer và kích thước connection pool.
//
// Mỗi lượt consume giữ một connection suốt transaction inbox/outbox. Số lượt đồng thời tối đa là
// (số consumer × ConcurrentMessageLimit). Vượt quá pool thì mọi thứ khác — kể cả health check —
// phải xếp hàng chờ connection, hết thời gian chờ rồi báo Unhealthy; retry lại làm đúng khối công
// việc đó, thành một cơn bão tự nuôi.
//
// Đây là lỗi ĐÃ XẢY RA trên production: pool 5 với 6 consumer ở mức đồng thời 4 = tối đa 24 lượt.
// Nó chỉ hiện ra khi outbox xả mạnh (lúc seed dữ liệu trình diễn), nên cấu hình sai nằm im rất lâu
// trước khi cắn. Nói to lúc khởi động, đúng lúc còn sửa được.
var consumerCount = typeof(Program).Assembly.GetTypes()
    .Count(t => t is { IsAbstract: false, IsInterface: false }
                && t.GetInterfaces().Any(i => i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(MassTransit.IConsumer<>)));
var poolSize = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).MaxPoolSize;
var peakConsumes = consumerCount * app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<TicketingOptions>>().Value.ConsumerConcurrency;

if (peakConsumes >= poolSize)
{
    app.Logger.LogWarning(
        "Cấu hình rủi ro: {Consumers} consumer × đồng thời {Concurrency} = {Peak} lượt consume có thể "
        + "chạy cùng lúc, trong khi connection pool chỉ có {Pool}. Pool sẽ cạn khi outbox xả mạnh và "
        + "health check db sẽ timeout. Hạ TICKETING__CONSUMERCONCURRENCY hoặc tăng 'Maximum Pool Size' "
        + "trong chuỗi kết nối (khuyến nghị: pool ≥ {Suggested}).",
        consumerCount, peakConsumes / Math.Max(consumerCount, 1), peakConsumes, poolSize, peakConsumes + 5);
}

// Migration + seed dữ liệu tham chiếu khi khởi động (mục 6.8, môi trường dev/compose).
// Dữ liệu TRÌNH DIỄN không nằm ở đây: nó chạy nền sau khi cổng đã mở — xem DemoDataHostedService.
await DatabaseInitializer.InitializeAsync(app.Services);

app.Run();

/// <summary>Điểm neo cho WebApplicationFactory trong integration test.</summary>
public partial class Program;
