using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    internal sealed class SlabThicknessZone
    {
        public readonly List<Point2d> Polygon;
        public readonly double ThicknessMm;
        public readonly string Name;

        public SlabThicknessZone(List<Point2d> polygon, double thicknessMm, string name)
        {
            Polygon = polygon;
            ThicknessMm = thicknessMm;
            Name = name;
        }
    }

    // Только геометрия: не меняет контуры, рёбра, узлы или элементы.
    internal static class SlabZoneCore
    {
        internal static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Cross(Point2d a, Point2d b, Point2d c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        private static double Parameter(Point2d p, Point2d a, Point2d b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy);
        }

        private static bool OnSegment(Point2d p, Point2d a, Point2d b)
        {
            double length = a.GetDistanceTo(b);
            if (length <= MeshTol.Zero) return p.GetDistanceTo(a) <= MeshTol.OnSegment;
            if (Math.Abs(Cross(a, b, p)) / length > MeshTol.OnSegment) return false;
            double t = Parameter(p, a, b), tol = MeshTol.OnSegment / length;
            return t >= -tol && t <= 1 + tol;
        }

        private static bool Inside(Point2d p, List<Point2d> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Point2d a = polygon[j], b = polygon[i];
                if (OnSegment(p, a, b)) return true;
                if ((a.Y > p.Y) != (b.Y > p.Y)
                    && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        // Точки пересечения на AB, включая касание вершины и коллинеарное наложение.
        private static void AddCuts(Point2d a, Point2d b, Point2d c, Point2d d, List<double> cuts)
        {
            double length = a.GetDistanceTo(b), otherLength = c.GetDistanceTo(d);
            if (length <= MeshTol.Zero || otherLength <= MeshTol.Zero) return;
            double rx = b.X - a.X, ry = b.Y - a.Y, sx = d.X - c.X, sy = d.Y - c.Y;
            double denom = rx * sy - ry * sx;
            if (Math.Abs(denom) > MeshTol.Zero * length * otherLength)
            {
                double qx = c.X - a.X, qy = c.Y - a.Y;
                double t = (qx * sy - qy * sx) / denom;
                double u = (qx * ry - qy * rx) / denom;
                double tol = MeshTol.OnSegment / length, otherTol = MeshTol.OnSegment / otherLength;
                if (t >= -tol && t <= 1 + tol && u >= -otherTol && u <= 1 + otherTol)
                    cuts.Add(Math.Max(0.0, Math.Min(1.0, t)));
            }
            if (OnSegment(c, a, b)) cuts.Add(Math.Max(0.0, Math.Min(1.0, Parameter(c, a, b))));
            if (OnSegment(d, a, b)) cuts.Add(Math.Max(0.0, Math.Min(1.0, Parameter(d, a, b))));
        }

        internal static bool ContainsPolygon(List<Point2d> element, List<Point2d> zone)
        {
            if (element.Count < 3 || zone.Count < 3) return false;
            for (int i = 0; i < element.Count; i++)
            {
                Point2d a = element[i], b = element[(i + 1) % element.Count];
                if (!Inside(a, zone)) return false;
                double length = a.GetDistanceTo(b);
                var cuts = new List<double> { 0.0, 1.0 };
                for (int k = 0; k < zone.Count; k++)
                    AddCuts(a, b, zone[k], zone[(k + 1) % zone.Count], cuts);
                cuts.Sort();
                // Проверяем каждый интервал: одна середина всего ребра пропустила бы
                // узкую выемку или выход через вершину вогнутого контура.
                for (int k = 1; k < cuts.Count; k++)
                {
                    if ((cuts[k] - cuts[k - 1]) * length <= MeshTol.OnSegment) continue;
                    double t = (cuts[k] + cuts[k - 1]) / 2.0;
                    if (!Inside(new Point2d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t), zone))
                        return false;
                }
            }
            return true;
        }

        internal static bool ValidatePolygon(List<Point2d> polygon, out string error)
        {
            error = "";
            if (polygon.Count < 3) { error = "нужно не менее трёх вершин"; return false; }
            double area2 = 0;
            Point2d origin = polygon[0];
            for (int i = 0; i < polygon.Count; i++)
            {
                Point2d a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                if (!Finite(a.X) || !Finite(a.Y)) { error = "неконечные координаты"; return false; }
                if (a.GetDistanceTo(b) <= MeshTol.NodeMerge) { error = "совпадающие соседние вершины"; return false; }
                area2 += Cross(origin, a, b);
                Point2d previous = polygon[(i + polygon.Count - 1) % polygon.Count];
                if (OnSegment(b, previous, a) || OnSegment(previous, a, b))
                { error = "соседние стороны накладываются"; return false; }
            }
            if (Math.Abs(area2) / 2.0 <= MeshTol.MinArea) { error = "вырожденная площадь"; return false; }
            for (int i = 0; i < polygon.Count; i++)
                for (int j = i + 1; j < polygon.Count; j++)
                {
                    if (j == i + 1 || (i == 0 && j == polygon.Count - 1)) continue;
                    Point2d a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                    Point2d c = polygon[j], d = polygon[(j + 1) % polygon.Count];
                    var cuts = new List<double>();
                    AddCuts(a, b, c, d, cuts);
                    if (cuts.Count > 0 || OnSegment(a, c, d) || OnSegment(b, c, d))
                    { error = "самопересечение или касание несмежных сторон"; return false; }
                }
            return true;
        }

        internal static BboxIndex IndexSegments(List<Point2d[]> segments)
        {
            var index = new BboxIndex(MeshTol.MinElementSize);
            for (int i = 0; i < segments.Count; i++) index.AddSegment(i, segments[i][0], segments[i][1]);
            return index;
        }

        internal static bool BoundaryCovered(List<Point2d> polygon, List<Point2d[]> segments,
            BboxIndex index, out int missingSide)
        {
            missingSide = -1;
            for (int i = 0; i < polygon.Count; i++)
            {
                Point2d a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                double length = a.GetDistanceTo(b), tol = MeshTol.OnSegment / length;
                var intervals = new List<double[]>();
                foreach (int j in index.Query(Math.Min(a.X, b.X) - MeshTol.OnSegment,
                    Math.Min(a.Y, b.Y) - MeshTol.OnSegment, Math.Max(a.X, b.X) + MeshTol.OnSegment,
                    Math.Max(a.Y, b.Y) + MeshTol.OnSegment))
                {
                    Point2d c = segments[j][0], d = segments[j][1];
                    if (Math.Abs(Cross(a, b, c)) / length > MeshTol.OnSegment
                        || Math.Abs(Cross(a, b, d)) / length > MeshTol.OnSegment) continue;
                    double t0 = Parameter(c, a, b), t1 = Parameter(d, a, b);
                    double start = Math.Max(0.0, Math.Min(t0, t1)), end = Math.Min(1.0, Math.Max(t0, t1));
                    if (end >= start) intervals.Add(new double[] { start, end });
                }
                intervals.Sort((x, y) => x[0].CompareTo(y[0]));
                double covered = 0;
                foreach (var interval in intervals)
                {
                    if (interval[0] > covered + tol) break;
                    covered = Math.Max(covered, interval[1]);
                }
                if (covered < 1.0 - tol) { missingSide = i; return false; }
            }
            return true;
        }

        internal static double FindThickness(List<Point2d> element, List<SlabThicknessZone> zones,
            BboxIndex index)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var p in element)
            {
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
            }
            double thickness = 0;
            string firstName = "";
            foreach (int i in index.Query(x0 - MeshTol.OnSegment, y0 - MeshTol.OnSegment,
                x1 + MeshTol.OnSegment, y1 + MeshTol.OnSegment))
            {
                var zone = zones[i];
                if (!ContainsPolygon(element, zone.Polygon)) continue;
                if (thickness > 0 && Math.Abs(thickness - zone.ThicknessMm) > MeshTol.Zero)
                    throw new InvalidOperationException($"Элемент целиком попал в зоны {firstName} и {zone.Name} с разной толщиной. Исправьте перекрытие контуров.");
                thickness = zone.ThicknessMm;
                firstName = zone.Name;
            }
            return thickness;
        }
    }
}
