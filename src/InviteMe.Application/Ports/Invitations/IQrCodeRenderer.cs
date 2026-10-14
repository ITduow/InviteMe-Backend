namespace InviteMe.Application.Ports.Invitations;

// Renders text as a QR code image; the adapter owns the QR library.
public interface IQrCodeRenderer
{
    byte[] RenderPng(string content);
    string RenderSvg(string content);
}
