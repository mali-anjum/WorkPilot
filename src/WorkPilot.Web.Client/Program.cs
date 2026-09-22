using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WorkPilot.Web.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Receives the founder's identity the server already persisted (WorkPilot.Web's
// PersistingAuthenticationStateProvider) so the WASM half of Auto render mode
// knows who is signed in without ever reading the (HttpOnly) session cookie
// itself (docs/specs/0004-auth-app-shell.md).
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, PersistentAuthenticationStateProvider>();

await builder.Build().RunAsync();
