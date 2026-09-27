using System.Security.Cryptography;
using System.Text;

namespace InviteMe.Infrastructure.Authentication;

public static class InvitationToken
{
    public static string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
