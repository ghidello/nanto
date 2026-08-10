namespace Nanto;

public readonly record struct WindowBounds
{
    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public WindowBounds(double x, double y, double width, double height)
    {
        ValidateFinite(x, nameof(x));
        ValidateFinite(y, nameof(y));
        ValidateFinite(width, nameof(width));
        ValidateFinite(height, nameof(height));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Window coordinates and dimensions must be finite.");
        }
    }
}