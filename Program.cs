using CatalogPilot.Components;
using CatalogPilot.Models;
using CatalogPilot.Options;
using CatalogPilot.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.Configure<EbayOptions>(builder.Configuration.GetSection(EbayOptions.SectionName));
builder.Services.Configure<EbayDeveloperAnalyticsOptions>(builder.Configuration.GetSection(EbayDeveloperAnalyticsOptions.SectionName));
builder.Services.Configure<ImageClassifierOptions>(builder.Configuration.GetSection(ImageClassifierOptions.SectionName));
builder.Services.Configure<LocalClassifierOptions>(builder.Configuration.GetSection(LocalClassifierOptions.SectionName));
builder.Services.Configure<EbayAccountDeletionOptions>(builder.Configuration.GetSection(EbayAccountDeletionOptions.SectionName));
builder.Services.Configure<ExternalBarcodeLookupOptions>(builder.Configuration.GetSection(ExternalBarcodeLookupOptions.SectionName));
builder.Services.Configure<GameCatalogStoreOptions>(builder.Configuration.GetSection(GameCatalogStoreOptions.SectionName));
builder.Services.Configure<AuthStoreOptions>(builder.Configuration.GetSection(AuthStoreOptions.SectionName));
builder.Services.Configure<InventoryStoreOptions>(builder.Configuration.GetSection(InventoryStoreOptions.SectionName));
builder.Services.Configure<StripeBillingOptions>(builder.Configuration.GetSection(StripeBillingOptions.SectionName));
builder.Services.Configure<SubscriptionEntitlementOptions>(builder.Configuration.GetSection(SubscriptionEntitlementOptions.SectionName));
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = builder.Configuration[$"{AuthStoreOptions.SectionName}:CookieName"] ?? "CatalogPilot.Auth";
        options.LoginPath = "/sign-in";
        options.AccessDeniedPath = "/sign-in";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IPhotoStorageService, LocalPhotoStorageService>();
builder.Services.AddScoped<RuleBasedVideoGameClassifierService>();
builder.Services.AddSingleton<IGameCatalogStore, SqliteGameCatalogStore>();
builder.Services.AddSingleton<IUserAccountStore, SqliteUserAccountStore>();
builder.Services.AddSingleton<IInventoryStore, SqliteInventoryStore>();
builder.Services.AddSingleton<IPasswordHashingService, Pbkdf2PasswordHashingService>();
builder.Services.AddSingleton<IEntitlementService, EntitlementService>();
builder.Services.AddHttpClient<VisionAssistedVideoGameClassifierService>();
builder.Services.AddHttpClient<IStripeBillingService, StripeBillingService>();
builder.Services.AddScoped<IGameTitleBankService, CatalogBackedGameTitleBankService>();
builder.Services.AddSingleton<IGameCodeBankService, LocalGameCodeBankService>();
builder.Services.AddHttpClient<IExternalBarcodeLookupService, ExternalBarcodeLookupService>();
builder.Services.AddScoped<LocalOcrVideoGameClassifierService>();
builder.Services.AddScoped<IBarcodeGameClassifierService, BarcodeGameClassifierService>();
builder.Services.AddScoped<IVideoGameClassifierService, BarcodeFirstVideoGameClassifierService>();
builder.Services.AddHttpClient<IEbayPricingService, EbayPricingService>();
builder.Services.AddHttpClient<IEbayListingService, EbayListingService>();
builder.Services.AddHttpClient<IEbayDeveloperAnalyticsService, EbayDeveloperAnalyticsService>();
var accountDeletionPath = builder.Configuration[$"{EbayAccountDeletionOptions.SectionName}:EndpointPath"];
if (string.IsNullOrWhiteSpace(accountDeletionPath))
{
    accountDeletionPath = "/api/ebay/account-deletion";
}

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var userStore = scope.ServiceProvider.GetRequiredService<IUserAccountStore>();
    await userStore.InitializeAsync();
    var inventoryStore = scope.ServiceProvider.GetRequiredService<IInventoryStore>();
    await inventoryStore.InitializeAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapPost("/account/register", async (
    HttpContext context,
    IUserAccountStore userStore,
    IPasswordHashingService passwordHashingService,
    IOptions<AuthStoreOptions> authStoreOptionsMonitor,
    CancellationToken cancellationToken) =>
{
    var form = await context.Request.ReadFormAsync(cancellationToken);
    var fullName = form["fullName"].ToString().Trim();
    var email = form["email"].ToString().Trim();
    var password = form["password"].ToString();
    var returnUrl = SanitizeReturnUrl(form["returnUrl"].ToString(), "/account");

    if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
    {
        return Results.Redirect($"/sign-up?error=invalid_email&returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
    {
        return Results.Redirect($"/sign-up?error=weak_password&returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    var hash = passwordHashingService.Hash(password);
    var createResult = await userStore.CreateUserAsync(email, fullName, hash, cancellationToken);
    if (!createResult.Success || createResult.User is null)
    {
        var errorCode = createResult.ErrorMessage.Contains("already", StringComparison.OrdinalIgnoreCase)
            ? "email_in_use"
            : "registration_failed";
        return Results.Redirect($"/sign-up?error={errorCode}&returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    await SignInAsync(context, createResult.User, isPersistent: true, authStoreOptionsMonitor.Value);
    return Results.Redirect(returnUrl);
}).DisableAntiforgery();
app.MapPost("/account/sign-in", async (
    HttpContext context,
    IUserAccountStore userStore,
    IPasswordHashingService passwordHashingService,
    IOptions<AuthStoreOptions> authStoreOptionsMonitor,
    CancellationToken cancellationToken) =>
{
    var form = await context.Request.ReadFormAsync(cancellationToken);
    var email = form["email"].ToString().Trim();
    var password = form["password"].ToString();
    var rememberMe = string.Equals(form["rememberMe"], "on", StringComparison.OrdinalIgnoreCase);
    var returnUrl = SanitizeReturnUrl(form["returnUrl"].ToString(), "/app");

    var user = await userStore.GetByEmailAsync(email, cancellationToken);
    if (user is null || !passwordHashingService.Verify(password, user.PasswordHash))
    {
        return Results.Redirect($"/sign-in?error=invalid_credentials&returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    await SignInAsync(context, user, rememberMe, authStoreOptionsMonitor.Value);
    return Results.Redirect(returnUrl);
}).DisableAntiforgery();
app.MapPost("/account/sign-out", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
}).RequireAuthorization().DisableAntiforgery();
app.MapPost("/billing/checkout", async (
    HttpContext context,
    IUserAccountStore userStore,
    IStripeBillingService stripeBillingService,
    IOptions<StripeBillingOptions> optionsMonitor,
    CancellationToken cancellationToken) =>
{
    if (!context.User.TryGetUserId(out var userId))
    {
        return Results.Redirect("/sign-in?returnUrl=%2Faccount");
    }

    var user = await userStore.GetByIdAsync(userId, cancellationToken);
    if (user is null)
    {
        return Results.Redirect("/sign-in?returnUrl=%2Faccount");
    }

    var options = optionsMonitor.Value;
    var successUrl = BuildAbsoluteUrl(context, options.CheckoutSuccessPath);
    var cancelUrl = BuildAbsoluteUrl(context, options.CheckoutCancelPath);

    var result = await stripeBillingService.CreateCheckoutSessionUrlAsync(
        user,
        successUrl,
        cancelUrl,
        cancellationToken);
    if (!result.Success)
    {
        return Results.Redirect($"/account?billingError={Uri.EscapeDataString(result.ErrorMessage)}");
    }

    return Results.Redirect(result.Url);
}).RequireAuthorization().DisableAntiforgery();
app.MapPost("/billing/portal", async (
    HttpContext context,
    IUserAccountStore userStore,
    IStripeBillingService stripeBillingService,
    IOptions<StripeBillingOptions> optionsMonitor,
    CancellationToken cancellationToken) =>
{
    if (!context.User.TryGetUserId(out var userId))
    {
        return Results.Redirect("/sign-in?returnUrl=%2Faccount");
    }

    var user = await userStore.GetByIdAsync(userId, cancellationToken);
    if (user is null)
    {
        return Results.Redirect("/sign-in?returnUrl=%2Faccount");
    }

    var portalReturnUrl = BuildAbsoluteUrl(context, optionsMonitor.Value.PortalReturnPath);
    var result = await stripeBillingService.CreatePortalSessionUrlAsync(user, portalReturnUrl, cancellationToken);
    if (!result.Success)
    {
        return Results.Redirect($"/account?billingError={Uri.EscapeDataString(result.ErrorMessage)}");
    }

    return Results.Redirect(result.Url);
}).RequireAuthorization().DisableAntiforgery();
app.MapPost("/api/stripe/webhook", async (
    HttpRequest request,
    IStripeBillingService stripeBillingService,
    CancellationToken cancellationToken) =>
{
    using var reader = new StreamReader(request.Body);
    var payload = await reader.ReadToEndAsync(cancellationToken);
    var signatureHeader = request.Headers["Stripe-Signature"].ToString();

    var result = await stripeBillingService.ProcessWebhookAsync(payload, signatureHeader, cancellationToken);
    if (!result.Authorized)
    {
        return Results.Unauthorized();
    }

    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
}).DisableAntiforgery();

app.MapGet(accountDeletionPath, (
    HttpContext context,
    IOptions<EbayAccountDeletionOptions> optionsMonitor) =>
{
    var options = optionsMonitor.Value;
    var challengeCode = context.Request.Query["challenge_code"].ToString();
    if (string.IsNullOrWhiteSpace(challengeCode))
    {
        return Results.BadRequest(new { error = "Missing challenge_code query parameter." });
    }

    if (string.IsNullOrWhiteSpace(options.VerificationToken))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Missing verification token",
            detail: "Set EbayAccountDeletion:VerificationToken in configuration.");
    }

    var endpoint = string.IsNullOrWhiteSpace(options.PublicEndpointUrl)
        ? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{context.Request.Path}"
        : options.PublicEndpointUrl.Trim();

    var challengeResponse = EbayAccountDeletionChallengeService.BuildChallengeResponse(
        challengeCode,
        options.VerificationToken,
        endpoint);

    return Results.Json(new { challengeResponse });
});

app.MapPost(accountDeletionPath, async (
    HttpRequest request,
    ILogger<Program> logger) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    logger.LogInformation(
        "Received eBay account deletion notification payload: {Payload}",
        body.Length <= 4000 ? body : body[..4000]);

    // Acknowledge immediately; process asynchronously in production if you persist user data.
    return Results.NoContent();
});

app.MapGet("/api/catalog/search", async (
    string? q,
    string? platform,
    int? max,
    IGameCatalogStore catalogStore,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return Results.BadRequest(new { error = "Missing q query parameter." });
    }

    var results = await catalogStore.SearchSimilarTitlesAsync(
        q,
        platform,
        max ?? 8,
        cancellationToken);

    return Results.Ok(results);
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/catalog/barcode/{code}", async (
    string code,
    string? platform,
    IGameCatalogStore catalogStore,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(code))
    {
        return Results.BadRequest(new { error = "Missing barcode value." });
    }

    var match = await catalogStore.FindByBarcodeAsync(code, platform, cancellationToken);
    return match is null
        ? Results.NotFound(new { error = "No catalog match found for barcode." })
        : Results.Ok(match);
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/catalog/stats", async (
    IGameCatalogStore catalogStore,
    CancellationToken cancellationToken) =>
{
    var titleCount = await catalogStore.CountTitlesAsync(cancellationToken);
    var curatedTitleCount = await catalogStore.CountCuratedTitlesAsync(cancellationToken);
    return Results.Ok(new
    {
        titleCount,
        curatedTitleCount
    });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/catalog/curated/rebuild", async (
    int? maxPerPlatform,
    IGameCatalogStore catalogStore,
    CancellationToken cancellationToken) =>
{
    var result = await catalogStore.RebuildCuratedCatalogAsync(maxPerPlatform ?? 2000, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.Problem(result.Message);
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/catalog/curated/platforms", async (
    int? max,
    IGameCatalogStore catalogStore,
    CancellationToken cancellationToken) =>
{
    var platforms = await catalogStore.GetCuratedPlatformSummaryAsync(max ?? 100, cancellationToken);
    return Results.Ok(platforms);
}).RequireAuthorization("AdminOnly");

if (app.Environment.IsDevelopment())
{
    app.MapGet("/api/dev/ebay/analytics/app-rate-limits", async (
        string? apiName,
        string? apiContext,
        IEbayDeveloperAnalyticsService analyticsService,
        CancellationToken cancellationToken) =>
    {
        var result = await analyticsService.GetAppRateLimitsAsync(apiName, apiContext, cancellationToken);
        return Results.Ok(result);
    });

    app.MapGet("/api/dev/ebay/analytics/user-rate-limits", async (
        string? apiName,
        string? apiContext,
        IEbayDeveloperAnalyticsService analyticsService,
        CancellationToken cancellationToken) =>
    {
        var result = await analyticsService.GetUserRateLimitsAsync(apiName, apiContext, cancellationToken);
        return Results.Ok(result);
    });

    app.MapGet("/api/dev/ebay/analytics/usage", async (
        string? apiName,
        string? apiContext,
        bool? includeUser,
        IEbayDeveloperAnalyticsService analyticsService,
        CancellationToken cancellationToken) =>
    {
        var shouldIncludeUser = includeUser ?? true;
        var appTask = analyticsService.GetAppRateLimitsAsync(apiName, apiContext, cancellationToken);
        Task<EbayRateLimitResponse>? userTask = null;
        if (shouldIncludeUser)
        {
            userTask = analyticsService.GetUserRateLimitsAsync(apiName, apiContext, cancellationToken);
            await Task.WhenAll(appTask, userTask);
        }
        else
        {
            await appTask;
        }

        var appResult = await appTask;
        var userResult = userTask is null ? null : await userTask;
        var warnings = new List<string>();

        if (!appResult.Success)
        {
            warnings.Add($"App rate limits unavailable: {appResult.Message}");
        }

        if (shouldIncludeUser && userResult is not null && !userResult.Success)
        {
            warnings.Add($"User rate limits unavailable: {userResult.Message}");
        }

        return Results.Ok(new
        {
            success = appResult.Success && (!shouldIncludeUser || userResult?.Success == true),
            retrievedUtc = DateTimeOffset.UtcNow,
            apiName = appResult.ApiName,
            apiContext = appResult.ApiContext,
            includeUser = shouldIncludeUser,
            warnings,
            appRateLimits = appResult,
            userRateLimits = userResult
        });
    });
}

app.Run();

static async Task SignInAsync(
    HttpContext context,
    AppUserRecord user,
    bool isPersistent,
    AuthStoreOptions authStoreOptions)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
        new(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName),
        new(ClaimTypes.Email, user.Email)
    };

    if (IsAdminUser(user.Email, authStoreOptions))
    {
        claims.Add(new Claim(ClaimTypes.Role, "Admin"));
    }

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);
    var authProperties = new AuthenticationProperties
    {
        IsPersistent = isPersistent,
        AllowRefresh = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(isPersistent ? 30 : 7)
    };

    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProperties);
}

static bool IsAdminUser(string email, AuthStoreOptions authStoreOptions)
{
    if (string.IsNullOrWhiteSpace(email))
    {
        return false;
    }

    var configuredAdminEmails = authStoreOptions.AdminEmails;
    if (configuredAdminEmails is null || configuredAdminEmails.Count == 0)
    {
        return false;
    }

    var normalizedEmail = email.Trim();
    return configuredAdminEmails.Any(adminEmail =>
        !string.IsNullOrWhiteSpace(adminEmail) &&
        string.Equals(adminEmail.Trim(), normalizedEmail, StringComparison.OrdinalIgnoreCase));
}

static string SanitizeReturnUrl(string? rawReturnUrl, string fallback)
{
    if (string.IsNullOrWhiteSpace(rawReturnUrl))
    {
        return fallback;
    }

    var candidate = rawReturnUrl.Trim();
    if (!candidate.StartsWith("/", StringComparison.Ordinal) ||
        candidate.StartsWith("//", StringComparison.Ordinal) ||
        candidate.StartsWith("/\\", StringComparison.Ordinal))
    {
        return fallback;
    }

    return candidate;
}

static string BuildAbsoluteUrl(HttpContext context, string configuredPathOrUrl)
{
    var value = string.IsNullOrWhiteSpace(configuredPathOrUrl) ? "/" : configuredPathOrUrl.Trim();
    if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
    {
        return absoluteUri.ToString();
    }

    if (!value.StartsWith("/", StringComparison.Ordinal))
    {
        value = "/" + value;
    }

    return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{value}";
}
