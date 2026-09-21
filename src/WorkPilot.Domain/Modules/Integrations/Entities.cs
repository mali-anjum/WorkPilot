using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Integrations;

public class Integration : Entity
{
    public required string ProviderName { get; set; }
    public required string Status { get; set; }
}

/// <summary>
/// A connected external account (Gmail, Calendar). Tokens are encrypted at
/// the application layer (ASP.NET Core Data Protection) before EF Core ever
/// writes them; a raw SELECT never returns a usable token (spec 0002, AC-6).
/// The properties below hold ciphertext once persisted.
/// </summary>
public class OAuthConnection : Entity
{
    public required Guid IntegrationId { get; init; }
    public required Guid ProfileId { get; init; }
    public required string AccessToken { get; set; }
    public required string RefreshToken { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
}
