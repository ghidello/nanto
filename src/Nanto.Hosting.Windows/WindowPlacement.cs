using Windows.Win32.Foundation;

namespace Nanto.Hosting.Windows;

internal static class WindowPlacement
{
    public static bool TryGetCorrection(RECT windowBounds, ReadOnlySpan<RECT> workAreas, out NativePosition position)
    {
        var windowWidth = GetLength(windowBounds.left, windowBounds.right, nameof(windowBounds));
        var windowHeight = GetLength(windowBounds.top, windowBounds.bottom, nameof(windowBounds));
        RECT? nearestWorkArea = null;
        var nearestDistance = ulong.MaxValue;

        foreach (var workArea in workAreas)
        {
            _ = GetLength(workArea.left, workArea.right, nameof(workAreas));
            _ = GetLength(workArea.top, workArea.bottom, nameof(workAreas));
            if (Intersects(windowBounds, workArea))
            {
                position = default;
                return false;
            }

            var distance = GetDistanceSquared(windowBounds, workArea);
            if (nearestWorkArea is null
                || distance < nearestDistance
                || distance == nearestDistance && Compare(workArea, nearestWorkArea.Value) < 0)
            {
                nearestWorkArea = workArea;
                nearestDistance = distance;
            }
        }

        if (nearestWorkArea is not { } target)
        {
            throw new InvalidOperationException("Windows did not report an accessible monitor work area.");
        }

        position = new NativePosition(
            GetCorrectedCoordinate(windowBounds.left, windowWidth, target.left, target.right),
            GetCorrectedCoordinate(windowBounds.top, windowHeight, target.top, target.bottom));
        return true;
    }

    private static int Compare(RECT left, RECT right)
    {
        var result = left.left.CompareTo(right.left);
        if (result != 0)
        {
            return result;
        }

        result = left.top.CompareTo(right.top);
        if (result != 0)
        {
            return result;
        }

        result = left.right.CompareTo(right.right);
        return result != 0 ? result : left.bottom.CompareTo(right.bottom);
    }

    private static long GetLength(int start, int end, string parameterName)
    {
        var length = (long)end - start;
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Native rectangles must have positive dimensions.");
        }

        return length;
    }

    private static int GetCorrectedCoordinate(int windowStart, long windowLength, int workAreaStart, int workAreaEnd)
    {
        var workAreaLength = (long)workAreaEnd - workAreaStart;
        if (windowLength >= workAreaLength)
        {
            return workAreaStart;
        }

        return checked((int)Math.Clamp((long)windowStart, workAreaStart, (long)workAreaEnd - windowLength));
    }

    private static ulong GetDistanceSquared(RECT windowBounds, RECT workArea)
    {
        var horizontalDistance = GetAxisDistance(windowBounds.left, windowBounds.right, workArea.left, workArea.right);
        var verticalDistance = GetAxisDistance(windowBounds.top, windowBounds.bottom, workArea.top, workArea.bottom);
        var horizontalSquared = horizontalDistance * horizontalDistance;
        var verticalSquared = verticalDistance * verticalDistance;
        return ulong.MaxValue - horizontalSquared < verticalSquared
            ? ulong.MaxValue
            : horizontalSquared + verticalSquared;
    }

    private static ulong GetAxisDistance(int firstStart, int firstEnd, int secondStart, int secondEnd)
    {
        if (firstEnd <= secondStart)
        {
            return checked((ulong)((long)secondStart - firstEnd));
        }

        return firstStart >= secondEnd
            ? checked((ulong)((long)firstStart - secondEnd))
            : 0;
    }

    private static bool Intersects(RECT left, RECT right) =>
        left.left < right.right
        && left.right > right.left
        && left.top < right.bottom
        && left.bottom > right.top;

    internal readonly record struct NativePosition(int X, int Y);
}