// Только для автономных тестов: ядру нужны координаты и расстояние, не AutoCAD.
using System;
namespace Autodesk.AutoCAD.Geometry
{
    public struct Point2d
    {
        public readonly double X, Y;
        public static readonly Point2d Origin = new Point2d(0, 0);
        public Point2d(double x, double y) { X = x; Y = y; }
        public double GetDistanceTo(Point2d other)
        {
            double dx = X - other.X, dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
