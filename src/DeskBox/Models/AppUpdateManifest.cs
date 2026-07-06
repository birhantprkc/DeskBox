namespace DeskBox.Models;

public sealed class AppUpdateManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Channel { get; set; } = "stable";
    public string Version { get; set; } = string.Empty;
    public string ReleaseDate { get; set; } = string.Empty;
    public string MinimumSupportedVersion { get; set; } = string.Empty;
    public bool Mandatory { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
    public string ManualDownloadUrl { get; set; } = string.Empty;
    public string MirrorUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string ReleaseNotesUrl { get; set; } = string.Empty;
    public Dictionary<string, string> Summary { get; set; } = [];

    public string GetLocalizedSummary(string cultureName)
    {
        if (Summary.Count == 0)
        {
            return string.Empty;
        }

        if (Summary.TryGetValue(cultureName, out string? exact) &&
            !string.IsNullOrWhiteSpace(exact))
        {
            return exact;
        }

        string language = cultureName.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? cultureName;
        var languageMatch = Summary.FirstOrDefault(pair =>
            pair.Key.StartsWith(language, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pair.Value));

        if (!string.IsNullOrWhiteSpace(languageMatch.Value))
        {
            return languageMatch.Value;
        }

        return Summary.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}

public enum AppUpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    InvalidManifest,
    Failed
}

public sealed class AppUpdateCheckResult
{
    public AppUpdateCheckResult(
        AppUpdateCheckStatus status,
        string currentVersion,
        AppUpdateManifest? manifest = null,
        string? errorMessage = null)
    {
        Status = status;
        CurrentVersion = currentVersion;
        Manifest = manifest;
        ErrorMessage = errorMessage;
    }

    public AppUpdateCheckStatus Status { get; }
    public string CurrentVersion { get; }
    public AppUpdateManifest? Manifest { get; }
    public string? ErrorMessage { get; }
    public bool IsUpdateAvailable => Status == AppUpdateCheckStatus.UpdateAvailable && Manifest is not null;
}

public sealed class AppUpdateDownloadProgress
{
    public AppUpdateDownloadProgress(long bytesReceived, long? totalBytes)
    {
        BytesReceived = bytesReceived;
        TotalBytes = totalBytes;
    }

    public long BytesReceived { get; }
    public long? TotalBytes { get; }
    public double Percent =>
        TotalBytes is > 0
            ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0, 100)
            : 0;
}

public enum AppUpdateDownloadFailureKind
{
    None,
    InvalidManifest,
    HashMissing,
    HashMismatch,
    Network,
    FileSystem
}

public sealed class AppUpdateDownloadResult
{
    private AppUpdateDownloadResult(
        bool success,
        string? filePath,
        AppUpdateDownloadFailureKind failureKind,
        string? errorMessage)
    {
        Success = success;
        FilePath = filePath;
        FailureKind = failureKind;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }
    public string? FilePath { get; }
    public AppUpdateDownloadFailureKind FailureKind { get; }
    public string? ErrorMessage { get; }

    public static AppUpdateDownloadResult Completed(string filePath) =>
        new(true, filePath, AppUpdateDownloadFailureKind.None, null);

    public static AppUpdateDownloadResult Failed(AppUpdateDownloadFailureKind failureKind, string? errorMessage = null) =>
        new(false, null, failureKind, errorMessage);
}

public sealed class AppUpdateInstallResult
{
    private AppUpdateInstallResult(bool success, string? errorMessage)
    {
        Success = success;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }
    public string? ErrorMessage { get; }

    public static AppUpdateInstallResult Started() => new(true, null);

    public static AppUpdateInstallResult Failed(string errorMessage) => new(false, errorMessage);
}
