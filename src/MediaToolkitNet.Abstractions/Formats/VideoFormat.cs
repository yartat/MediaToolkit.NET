namespace MediaToolkitNet.Abstractions.Formats;

/// <summary>
/// Description of an uncompressed video stream.
/// </summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="PixelFormat">Raw buffer layout.</param>
/// <param name="FrameRate">Nominal frame rate; may be zero for variable frame rate sources.</param>
public readonly record struct VideoFormat(int Width, int Height, PixelFormat PixelFormat, Rational FrameRate)
{
    /// <summary>Creates a format with an integral frame rate.</summary>
    public VideoFormat(int width, int height, PixelFormat pixelFormat, int frameRate)
        : this(width, height, pixelFormat, new Rational(frameRate, 1))
    {
    }

    /// <summary>True when the format is fully specified and usable.</summary>
    public bool IsValid => Width > 0 && Height > 0 && PixelFormat != PixelFormat.Unknown;

    /// <summary>Total unpadded frame size in bytes, or 0 when not derivable.</summary>
    public int FrameSize => PixelFormat.FrameSize(Width, Height);

    /// <inheritdoc />
    public override string ToString() => $"{Width}x{Height} {PixelFormat} @ {FrameRate}";
}

/// <summary>
/// Exact rational number, mirroring <c>AVRational</c>. Used for frame rates and
/// time bases where floating point rounding is not acceptable.
/// </summary>
/// <param name="Numerator">Numerator.</param>
/// <param name="Denominator">Denominator; zero means "undefined".</param>
public readonly record struct Rational(int Numerator, int Denominator)
{
    /// <summary>The undefined rational 0/0.</summary>
    public static Rational Zero => default;

    /// <summary>Approximate value as a double; returns 0 for an undefined rational.</summary>
    public double Value => Denominator == 0 ? 0d : (double)Numerator / Denominator;

    /// <summary>Swapped numerator and denominator.</summary>
    public Rational Inverse => new(Denominator, Numerator);

    /// <summary>Creates a rational from a decimal frame rate, e.g. 29.97 becomes 30000/1001.</summary>
    public static Rational FromDouble(double value, int maxDenominator = 1001)
    {
        if (value <= 0d)
        {
            return Zero;
        }

        // Try the broadcast rates first: they must round-trip exactly.
        var scaled = value * 1001d;
        var rounded = Math.Round(scaled);
        if (Math.Abs(scaled - rounded) < 1e-6 && rounded <= int.MaxValue)
        {
            return new Rational((int)rounded, 1001).Reduce();
        }

        return new Rational((int)Math.Round(value * maxDenominator), maxDenominator).Reduce();
    }

    /// <summary>Returns the rational reduced by the greatest common divisor.</summary>
    public Rational Reduce()
    {
        var a = Math.Abs(Numerator);
        var b = Math.Abs(Denominator);
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? this : new Rational(Numerator / a, Denominator / a);
    }

    /// <inheritdoc />
    public override string ToString() => Denominator == 0 ? "n/a" : $"{Numerator}/{Denominator}";
}
