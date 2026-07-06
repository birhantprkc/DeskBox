namespace DeskBox.Services;

public sealed class DeskBoxDataPathService
{
    public static DeskBoxDataPathService Current { get; } = new();

    public DeskBoxDataPathService(string? rootPath = null)
    {
        RootPath = string.IsNullOrWhiteSpace(rootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBox")
            : rootPath;
    }

    public string RootPath { get; }
    public string DataDirectory => Path.Combine(RootPath, "data");
    public string UpdatesDirectory => Path.Combine(RootPath, "updates");
    public string LogFilePath => Path.Combine(RootPath, "DeskBox.log");
}
