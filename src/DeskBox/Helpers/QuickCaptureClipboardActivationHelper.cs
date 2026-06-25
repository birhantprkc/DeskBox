using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.Helpers;

public static class QuickCaptureClipboardActivationHelper
{
    public static async Task<bool> EnableAsync(XamlRoot? xamlRoot, LocalizationService localizationService)
    {
        var settingsService = App.Current.SettingsService;
        settingsService.Settings.QuickCaptureEnabled = true;
        settingsService.Settings.QuickCaptureClipboardEnabled = true;
        await settingsService.SaveAsync();
        App.Current.QuickCaptureClipboardService?.Refresh();
        App.Current.QuickCaptureClipboardService?.CaptureCurrent();
        App.Log("[QuickCaptureClipboard] Enabled");
        return true;
    }
}
