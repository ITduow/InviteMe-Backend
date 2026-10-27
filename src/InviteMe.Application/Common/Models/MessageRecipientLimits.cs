namespace InviteMe.Application.Common.Models;

public static class MessageRecipientLimits
{
    // Delivery workers claim small groups of individual jobs; selection need not stop
    // at the former 180-guest demo limit. Keep a finite bound on each queued request.
    public const int Maximum = 5000;
}
