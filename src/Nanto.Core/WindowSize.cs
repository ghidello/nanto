namespace Nanto;

/// <summary>
/// Describes the size of a window's client content area in device-independent pixels.
/// </summary>
public readonly record struct WindowSize
{
    public double Width { get; }

    public double Height { get; }

    public WindowSize(double width, double height)
    {
        ValidateDimension(width, nameof(width));
        ValidateDimension(height, nameof(height));

        Width = width;
        Height = height;
    }

    private static void ValidateDimension(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Window dimensions must be finite and positive.");
        }
    }
}
