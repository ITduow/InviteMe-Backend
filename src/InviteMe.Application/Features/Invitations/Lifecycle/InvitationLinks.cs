namespace InviteMe.Application.Features.Invitations.Lifecycle;

// Path of the public invitation page on the frontend; the token is the last segment.
public static class InvitationLinks
{
    public static string PublicPath(string token) => "/invitation/" + token;
}
