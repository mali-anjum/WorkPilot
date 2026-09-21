using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace WorkPilot.Infrastructure.Persistence;

/// <summary>
/// Encrypts a string column with ASP.NET Core Data Protection before it
/// reaches Postgres, and decrypts it on read. Used for
/// <c>OAuthConnection.AccessToken</c>/<c>RefreshToken</c> so a raw SELECT
/// never returns a usable token (spec 0002, AC-6).
/// </summary>
public sealed class EncryptedStringConverter(IDataProtector protector)
    : ValueConverter<string, string>(
        plainText => protector.Protect(plainText),
        cipherText => protector.Unprotect(cipherText));
