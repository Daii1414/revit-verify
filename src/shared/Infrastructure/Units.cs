namespace RvtMcp.Plugin
{
    /// <summary>
    /// Shared unit conversions. Revit works internally in feet; every tool boundary
    /// speaks millimetres. Keeping the factors in one place stops the values from
    /// drifting apart between handlers.
    /// </summary>
    public static class Units
    {
        /// <summary>Feet to millimetres.</summary>
        public const double FeetToMm = 304.8;

        /// <summary>Square feet to square metres.</summary>
        public const double SquareFeetToSquareMeters = 0.092903043596;

        /// <summary>Cubic feet to cubic metres.</summary>
        public const double CubicFeetToCubicMeters = 0.028316846592;

        /// <summary>Feet to millimetres as a value.</summary>
        public static double Mm(double feet) => feet * FeetToMm;

        /// <summary>Square feet to square metres as a value.</summary>
        public static double SquareMeters(double squareFeet) => squareFeet * SquareFeetToSquareMeters;

        /// <summary>Cubic feet to cubic metres as a value.</summary>
        public static double CubicMeters(double cubicFeet) => cubicFeet * CubicFeetToCubicMeters;
    }
}
