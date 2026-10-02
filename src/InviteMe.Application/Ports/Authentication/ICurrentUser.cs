namespace InviteMe.Application.Ports.Authentication;

public interface ICurrentUser
{
    Guid? UserId { get; }
}
