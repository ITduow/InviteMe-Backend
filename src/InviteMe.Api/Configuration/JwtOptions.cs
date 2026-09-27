namespace InviteMe.Api.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; init; } = "";
    public string Audience { get; init; } = "";
    // Base64-encoded random bytes, at least 256 bits.
    public string SigningKey { get; init; } = "";
    public int ExpirationMinutes { get; init; } = 15;

    public bool IsValid()
    {
        if (string.IsNullOrWhiteSpace(SigningKey) || string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience)
            || ExpirationMinutes is < 1 or > 60)
            return false;
        try { return Convert.FromBase64String(SigningKey).Length >= 32; }
        catch (FormatException) { return false; }
    }
}
