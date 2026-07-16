using CommunityToolkit.WinUI.Animations;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow
{
    private void SetupStep6()
    {
        // Storage summary
        string path = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        var pinState = ExplorerQuickAccessHelper.GetQuickAccessPinState(path, out _);
        bool isPinned = pinState == QuickAccessPinState.Pinned;
        string pinStatus = isPinned
            ? _localizationService.T("Onboarding.Step6.SummaryPinned")
            : _localizationService.T("Onboarding.Step6.SummaryNotPinned");
        Step6StorageSummary.Text = $"{System.IO.Path.GetFileName(path)} · {pinStatus}";

        // Appearance summary
        string themeLabel = _settingsService.Settings.Theme switch
        {
            "Light" => _localizationService.T("Onboarding.Step3.ThemeLight"),
            "Dark" => _localizationService.T("Onboarding.Step3.ThemeDark"),
            _ => _localizationService.T("Onboarding.Step3.ThemeSystem")
        };
        string materialLabel = _settingsService.Settings.WidgetMaterialType switch
        {
            "Acrylic" => _localizationService.T("Onboarding.Step3.MaterialAcrylic"),
            "Solid" => _localizationService.T("Onboarding.Step3.MaterialSolid"),
            _ => _localizationService.T("Onboarding.Step3.MaterialMica")
        };
        Step6AppearanceSummary.Text = $"{themeLabel} · {materialLabel}";

        // Widgets summary
        var enabledWidgets = new List<string>();
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Todo))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step4.TodoTitle"));
        }
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.QuickCapture))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step4.QuickCaptureTitle"));
        }
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Music))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step4.MusicTitle"));
        }
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Weather))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step4.WeatherTitle"));
        }
        Step6WidgetsSummary.Text = enabledWidgets.Count > 0
            ? string.Join(" · ", enabledWidgets)
            : _localizationService.T("Onboarding.Step6.NoWidgets");

        // Daily use summary
        string hotkeySummary = _settingsService.Settings.GlobalHotkeyEnabled
            ? _localizationService.T("Onboarding.Step6.SummaryHotkeyOn")
            : _localizationService.T("Onboarding.Step6.SummaryHotkeyOff");
        string startupSummary = StartupService.IsEnabled()
            ? _localizationService.T("Onboarding.Step6.SummaryStartupOn")
            : _localizationService.T("Onboarding.Step6.SummaryStartupOff");
        Step6DailySummary.Text = $"{hotkeySummary} · {startupSummary}";
    }

    // ════════════════════════════════════════════════════════════
    //  Localization
    // ════════════════════════════════════════════════════════════

    private void OnLanguageChanged()
    {
        Title = _localizationService.T("Onboarding.WindowTitle");
        Localized.RefreshAll(_localizationService);
        PrepareIntroContent();
        SetupStep(animate: false);
        UpdateFooterState();
    }

    // ════════════════════════════════════════════════════════════
    //  Intro Sequence (preserved from original)
    // ════════════════════════════════════════════════════════════
}
