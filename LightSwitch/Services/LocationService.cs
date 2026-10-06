using Windows.Devices.Geolocation;

namespace LightSwitch.Services;

// Gets the device's approximate location via Windows Location Services —
// the same source the system Night Light schedule uses for sunset/sunrise.
internal static class LocationService
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<(double Latitude, double Longitude)?> TryGetLocationAsync()
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                Logger.Warn("[Location] Access denied (system location permission is off).");
                return null;
            }

            // Coarse accuracy is plenty for sunrise/sunset calculation
            var geolocator = new Geolocator { DesiredAccuracyInMeters = 5000 };
            var position = await geolocator.GetGeopositionAsync(MaxAge, Timeout);
            var basic = position.Coordinate.Point.Position;
            return (basic.Latitude, basic.Longitude);
        }
        catch (Exception e)
        {
            Logger.Warn("[Location] Failed to get location: " + e.Message);
            return null;
        }
    }
}
