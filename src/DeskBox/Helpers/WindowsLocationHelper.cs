using System.Runtime.InteropServices;
using Windows.Devices.Geolocation;
using Windows.Foundation;

namespace DeskBox.Helpers;

/// <summary>
/// Uses the Windows Geolocation API to get the current device location.
/// Falls back to a default location (Beijing) if permission is denied or unavailable.
/// </summary>
public static class WindowsLocationHelper
{
    private static bool? s_permissionAsked;

    /// <summary>
    /// Gets the current location using the Windows Geolocation API.
    /// Returns (latitude, longitude, displayName).
    /// Falls back to Beijing if location access is unavailable.
    /// </summary>
    public static async Task<(double Lat, double Lon, string Name)> GetLocationAsync(
        Services.LocalizationService? localizationService = null)
    {
        try
        {
            var accessStatus = await Geolocator.RequestAccessAsync();
            if (accessStatus != GeolocationAccessStatus.Allowed)
            {
                App.Log($"[WindowsLocation] Access not allowed: {accessStatus}");
                return GetDefaultLocation(localizationService);
            }

            var geolocator = new Geolocator
            {
                DesiredAccuracy = PositionAccuracy.Default,
                DesiredAccuracyInMeters = 5000
            };

            var position = await geolocator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(30),
                timeout: TimeSpan.FromSeconds(10));

            double lat = position.Coordinate.Point.Position.Latitude;
            double lon = position.Coordinate.Point.Position.Longitude;

            // Try reverse geocoding to get a friendly name
            string name = await TryReverseGeocodeAsync(lat, lon, localizationService);

            App.Log($"[WindowsLocation] Got location lat={lat:F4} lon={lon:F4} name={name}");
            return (lat, lon, name);
        }
        catch (Exception ex)
        {
            App.Log($"[WindowsLocation] Failed: {ex.Message}");
            return GetDefaultLocation(localizationService);
        }
    }

    private static (double, double, string) GetDefaultLocation(
        Services.LocalizationService? localizationService)
    {
        bool isEnglish = localizationService?.IsEnglish ?? false;
        return (39.9042, 116.4074, isEnglish ? "Beijing" : "\u5317\u4EAC");
    }

    /// <summary>
    /// Uses the Open-Meteo reverse geocoding API to get a city name from coordinates.
    /// </summary>
    private static async Task<string> TryReverseGeocodeAsync(
        double lat, double lon, Services.LocalizationService? localizationService)
    {
        try
        {
            string language = localizationService?.IsEnglish == true ? "en" : "zh";
            string url = $"https://geocoding-api.open-meteo.com/v1/search?latitude={lat:F4}&longitude={lon:F4}&count=1&language={language}&format=json";

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            string json = await client.GetStringAsync(url);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results) &&
                results.GetArrayLength() > 0)
            {
                var first = results[0];
                string? name = first.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                string? admin1 = first.TryGetProperty("admin1", out var adminProp) ? adminProp.GetString() : null;
                string? country = first.TryGetProperty("country", out var countryProp) ? countryProp.GetString() : null;

                if (!string.IsNullOrEmpty(name))
                {
                    if (!string.IsNullOrEmpty(admin1) && admin1 != name)
                    {
                        return $"{name}, {admin1}";
                    }
                    return name!;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WindowsLocation] Reverse geocode failed: {ex.Message}");
        }

        return $"{lat:F2}, {lon:F2}";
    }
}
