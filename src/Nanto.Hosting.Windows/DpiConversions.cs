namespace Nanto.Hosting.Windows;

internal static class DpiConversions
{
    public const uint DefaultDpi = 96;

    public static int ToPixels(double value, uint dpi, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfZero(dpi);
        try
        {
            return checked((int)Math.Round(value * dpi / DefaultDpi, MidpointRounding.AwayFromZero));
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The DIP value cannot be represented by Win32 pixels at the current DPI.");
        }
    }

    public static double ToDips(int value, uint dpi)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfZero(dpi);
        return (double)value * DefaultDpi / dpi;
    }
}
