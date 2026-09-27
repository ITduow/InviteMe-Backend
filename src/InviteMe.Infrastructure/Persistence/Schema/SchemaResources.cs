namespace InviteMe.Infrastructure.Persistence.Schema;

public static class SchemaResources
{
    public static string Read(string name)
    {
        using var stream = typeof(SchemaResources).Assembly.GetManifestResourceStream($"InviteMe.Schema.{name}")
            ?? throw new InvalidOperationException($"Missing schema resource: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string BaselineForMigration() => Read("Baseline.sql")
        // EF owns the outer transaction. Keep the original schema file byte-for-byte.
        .Replace("\r\n", "\n")
        .Replace("\nBEGIN;\n", "\n")
        .Replace("\nCOMMIT;\n", "\n")
        .Replace("SET search_path TO inviteme, public;", "SET LOCAL search_path TO inviteme, public;");
}
