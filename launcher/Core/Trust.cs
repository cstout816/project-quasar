namespace Quasar;

public static class Trust
{
    public static string? PublicKey
    {
        get
        {
            using var stream = typeof(Trust).Assembly.GetManifestResourceStream("Quasar.ReleasePublicKey.pem");
            if (stream is null)
                return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
