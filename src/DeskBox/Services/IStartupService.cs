namespace DeskBox.Services;

public interface IStartupService
{
    bool IsEnabled();
    string? GetRunValue();
    void Enable();
    void Disable();
    void SetEnabled(bool enabled);
}
