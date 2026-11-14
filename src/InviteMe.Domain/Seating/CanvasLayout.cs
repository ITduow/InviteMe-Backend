namespace InviteMe.Domain.Seating;

public sealed record CanvasPoint(double X, double Y);
public sealed record CanvasZone(double X, double Y, double Width, double Height);
public sealed record CanvasTable(double X, double Y, bool OutsideZone = false, string? Side = null);
public sealed record CanvasObject(string Id, string Type, double X, double Y);
public sealed record CanvasLayout(bool SplitZones, Dictionary<string, CanvasTable> Tables,
    Dictionary<string, CanvasZone> Zones, CanvasPoint Stage, CanvasObject[] Objects);
