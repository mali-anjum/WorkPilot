using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Supabase;
using WorkPilot.Application.Modules.Identity;
using WorkPilot.Infrastructure.Modules.Identity;
using WorkPilot.Web;
using WorkPilot.Web.Components;
using WorkPilot.Web.Features.Auth;

var builder = WebApplication.CreateBuilder(args);

// Aspire cross-cutting concerns: OpenTelemetry, health checks, service
// discovery, resilient HttpClient defaults.
builder.AddServiceDefaults();

// Fail fast (AGENTS.md) rather than a null reference deep in a sign in
// request: both are required for the Supabase .NET client to reach GoTrue.
var supabaseUrl = builder.Configuration["Supabase:Url"]
    ?? throw new InvalidOperationException("Configuration \"Supabase:Url\" is required (the self hosted Supabase stack's base URL).");
var supabaseAnonKey = builder.Configuration["Supabase:AnonKey"]
    ?? throw new InvalidOperationException("Configuration \"Supabase:AnonKey\" is required.");

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// Carries the signed in founder's identity from the server's cookie auth
// across to the WASM half of Auto render mode, without WASM ever touching
// the (HttpOnly) cookie itself. Paired with PersistentAuthenticationStateProvider
// in WorkPilot.Web.Client's Program.cs.
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, PersistingAuthenticationStateProvider>();

// The session cookie: a plain, self issued identity cookie (docs/specs/0004-auth-app-shell.md,
// Option 1b). It never carries a GoTrue token, only the founder's identity
// claims, so there is no refresh-token subsystem to build. Sliding expiry
// keeps a daily-use session alive for up to 30 days of activity (AC-3); an
// expired, missing, or undecryptable cookie sends an anonymous request to
// /login with a returnUrl (AC-1, AC-4), handled by RequireAuthorization()
// below plus this scheme's LoginPath.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ReturnUrlParameter = "returnUrl";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.Name = "workpilot-session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();

// Data Protection key ring, encrypting the session cookie above. Default key
// storage (~/.aspnet/DataProtection-Keys on Linux) is fine for this single
// instance VPS deployment (same call as WorkPilot.Api makes for
// OAuthConnection tokens, see its Program.cs): it survives an ordinary
// process restart, only a container image swap without a mounted volume
// would lose it, which does not apply to this deployment model.
builder.Services.AddDataProtection();

// GoTrue, via the official Supabase .NET client; nothing else in this
// project talks to it directly except WorkPilot.Infrastructure's
// SupabaseAuthService (docs/specs/0004-auth-app-shell.md).
builder.Services.AddScoped(_ => new Client(supabaseUrl, supabaseAnonKey, new SupabaseOptions
{
    // Only Auth is used (see IAuthService above); Realtime is a persistent
    // websocket this request-scoped client has no business opening.
    AutoConnectRealtime = false,
    AutoRefreshToken = false,
    // This stack has no Kong gateway in front of GoTrue (deferred per
    // supabase/docker-compose.yml's header), so Supabase:Url already points
    // directly at GoTrue's own root; the client's default "{0}/auth/v1"
    // template assumes a gateway routing prefix that doesn't exist here.
    AuthUrlFormat = "{0}",
}));
builder.Services.AddScoped<IAuthService, SupabaseAuthService>();

// The Api project is the sole owner of WorkPilotDbContext; the Web host
// resolves/creates the founder's ProfileId by calling its internal endpoint
// over the Aspire service discovery network, never by touching EF Core here.
builder.Services.AddHttpClient("api", client => client.BaseAddress = new Uri("https+http://api"));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
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

app.MapAuthEndpoints();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(WorkPilot.Web.Client._Imports).Assembly)
    // Every route the Router discovers requires a valid session (docs/specs/0004-auth-app-shell.md,
    // AC-1, AC-7): an unauthenticated request never reaches Blazor rendering
    // at all, it is redirected to /login by the cookie scheme's LoginPath
    // before any shell content renders, even partially.
    .RequireAuthorization();

app.MapDefaultEndpoints(); // Aspire health/liveness endpoints from ServiceDefaults

app.Run();
