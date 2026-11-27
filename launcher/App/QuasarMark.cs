namespace Quasar.Launcher;

internal static class QuasarMark
{
    public static Icon LoadIcon()
    {
        using var source = typeof(QuasarMark).Assembly.GetManifestResourceStream("Quasar.Icon")!;
        using var icon = new Icon(source, 48, 48);
        return (Icon)icon.Clone();
    }
}
