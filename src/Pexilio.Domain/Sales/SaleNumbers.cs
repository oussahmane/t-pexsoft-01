using System.Globalization;

namespace Pexilio.Domain.Sales;

public static class SaleNumbers
{
    public static string Format(long value) => "V-" + Pad(value);

    public const string ReturnSeries = "sale_return";

    public static string FormatReturn(long value) => "AV-" + Pad(value);

    private static string Pad(long value)
    {
        var digits = value.ToString(CultureInfo.InvariantCulture);
        return digits.Length < 6 ? digits.PadLeft(6, '0') : digits;
    }
}
