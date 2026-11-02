namespace FactoryWorld.Launcher;

internal static class Program
{
    /// <summary>
    /// The game goes to %LOCALAPPDATA%\FactoryWorld3DGame (no administrator rights needed), or where --dir says.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        SelfUpdate.CleanUp();
        var at = Array.IndexOf(args, "--dir");
        var directory = at >= 0 && at + 1 < args.Length
            ? Path.GetFullPath(args[at + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryWorld3DGame");
        ApplicationConfiguration.Initialize();
        Application.Run(new LauncherForm(directory));
    }
}
