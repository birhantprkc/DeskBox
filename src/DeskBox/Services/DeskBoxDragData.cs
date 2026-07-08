using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Services;

public static class DeskBoxDragData
{
    public const string TextFormat = "DeskBox.Internal.Text.v1";
    public const string SourceFormat = "DeskBox.Internal.Source.v1";
    public const string SourceTodo = "todo";
    public const string SourceQuickCapture = "quick-capture";

    public static void SetText(DataPackage dataPackage, string? text, string source)
    {
        string normalizedText = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return;
        }

        dataPackage.SetText(normalizedText);
        dataPackage.SetData(TextFormat, normalizedText);
        dataPackage.SetData(SourceFormat, source);
    }

    public static async Task<string?> TryGetTextAsync(DataPackageView dataView)
    {
        string? internalText = await TryGetInternalTextAsync(dataView);
        if (!string.IsNullOrWhiteSpace(internalText))
        {
            return internalText;
        }

        if (dataView.Contains(StandardDataFormats.Text))
        {
            string text = await dataView.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return NormalizeText(text);
            }
        }

        if (dataView.Contains(StandardDataFormats.WebLink))
        {
            var link = await dataView.GetWebLinkAsync();
            if (!string.IsNullOrWhiteSpace(link?.AbsoluteUri))
            {
                return link.AbsoluteUri;
            }
        }

        return null;
    }

    public static async Task<string?> TryGetInternalTextAsync(DataPackageView dataView)
    {
        if (!dataView.Contains(TextFormat))
        {
            return null;
        }

        try
        {
            if (await dataView.GetDataAsync(TextFormat) is string internalText &&
                !string.IsNullOrWhiteSpace(internalText))
            {
                return NormalizeText(internalText);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to read DeskBox internal text: {ex.Message}");
        }

        return null;
    }

    private static string NormalizeText(string? text)
    {
        return (text ?? string.Empty).Trim();
    }
}
