namespace LightSwitch.Services;

public readonly record struct SunTimes(int SunriseHour, int SunriseMinute, int SunsetHour, int SunsetMinute);

// Sunrise/sunset calculation (same algorithm as the original C++ LightSwitch module).
internal static class SunCalculator
{
    public static SunTimes Calculate(double latitude, double longitude, int year, int month, int day)
    {
        const double zenith = 90.833;

        int n1 = (int)Math.Floor(275.0 * month / 9.0);
        int n2 = (int)Math.Floor((month + 9.0) / 12.0);
        int n3 = (int)Math.Floor(1.0 + Math.Floor((year - 4.0 * Math.Floor(year / 4.0) + 2.0) / 3.0));
        int n = n1 - (n2 * n3) + day - 30;

        double CalcTime(bool sunrise)
        {
            double lngHour = longitude / 15.0;
            double t = sunrise ? n + ((6 - lngHour) / 24) : n + ((18 - lngHour) / 24);

            double m = (0.9856 * t) - 3.289;
            double l = m + (1.916 * Math.Sin(Deg2Rad(m))) + (0.020 * Math.Sin(2 * Deg2Rad(m))) + 282.634;
            if (l < 0) l += 360;
            if (l > 360) l -= 360;

            double ra = Rad2Deg(Math.Atan(0.91764 * Math.Tan(Deg2Rad(l))));
            if (ra < 0) ra += 360;
            if (ra > 360) ra -= 360;

            double lQuadrant = Math.Floor(l / 90) * 90;
            double raQuadrant = Math.Floor(ra / 90) * 90;
            ra += lQuadrant - raQuadrant;
            ra /= 15;

            double sinDec = 0.39782 * Math.Sin(Deg2Rad(l));
            double cosDec = Math.Cos(Math.Asin(sinDec));

            double cosH = (Math.Cos(Deg2Rad(zenith)) - (sinDec * Math.Sin(Deg2Rad(latitude)))) /
                          (cosDec * Math.Cos(Deg2Rad(latitude)));
            if (cosH > 1 || cosH < -1)
                return -1;

            double h = sunrise ? 360 - Rad2Deg(Math.Acos(cosH)) : Rad2Deg(Math.Acos(cosH));
            h /= 15;

            double tt = h + ra - (0.06571 * t) - 6.622;
            double ut = tt - lngHour;
            while (ut < 0) ut += 24;
            while (ut >= 24) ut -= 24;

            return ut;
        }

        double riseUt = CalcTime(true);
        double setUt = CalcTime(false);

        // Convert UTC hour-of-day to local time using the local zone's offset for that date.
        (int hour, int minute) ToLocal(double ut)
        {
            var utc = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc).AddHours(ut);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
            return (local.Hour, local.Minute);
        }

        var (riseHour, riseMinute) = ToLocal(riseUt);
        var (setHour, setMinute) = ToLocal(setUt);

        return new SunTimes(riseHour, riseMinute, setHour, setMinute);
    }

    private static double Deg2Rad(double deg) => deg * Math.PI / 180.0;

    private static double Rad2Deg(double rad) => rad * 180.0 / Math.PI;
}
