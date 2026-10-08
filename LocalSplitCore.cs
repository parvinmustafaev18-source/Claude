using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    internal sealed class LocalSplitCut
    {
        public readonly double Parameter;
        public readonly Point2d Point;
        public LocalSplitCut(double parameter, Point2d point)
        {
            Parameter = parameter;
            Point = point;
        }
    }

    // Результат привязан к ИСХОДНЫМ рёбрам: запись в DWG меняет только их.
    // Здесь нет ни построения сетки, ни перемещения существующих узлов.
    internal static class LocalSplitCore
    {
        public static Dictionary<int, List<LocalSplitCut>> Calculate(
            List<Point2d[]> segments, HashSet<int> selected)
        {
            var result = new Dictionary<int, List<LocalSplitCut>>();
            var bounds = new BboxIndex(MeshTol.MinElementSize);
            for (int i = 0; i < segments.Count; i++)
                bounds.AddSegment(i, segments[i][0], segments[i][1]);

            var pairs = new HashSet<long>();
            var seeds = new List<int>(selected);
            seeds.Sort();
            foreach (int i in seeds)
            {
                Point2d a = segments[i][0], b = segments[i][1];
                foreach (int j in bounds.Query(
                    Math.Min(a.X, b.X) - MeshTol.OnSegment,
                    Math.Min(a.Y, b.Y) - MeshTol.OnSegment,
                    Math.Max(a.X, b.X) + MeshTol.OnSegment,
                    Math.Max(a.Y, b.Y) + MeshTol.OnSegment))
                {
                    if (i == j) continue;
                    long key = ((long)Math.Min(i, j) << 32) | (uint)Math.Max(i, j);
                    if (!pairs.Add(key)) continue;
                    Point2d c = segments[j][0], d = segments[j][1];
                    Point2d cross;
                    if (TryCrossing(a, b, c, d, out cross))
                    {
                        AddCut(result, i, a, b, cross);
                        AddCut(result, j, c, d, cross);
                    }
                    // Т-примыкания и концы коллинеарных отрезков.
                    // Совпадающие объекты не удаляются: их свойства принадлежат инженеру.
                    AddEndpoint(result, i, a, b, c);
                    AddEndpoint(result, i, a, b, d);
                    AddEndpoint(result, j, c, d, a);
                    AddEndpoint(result, j, c, d, b);
                }
            }
            foreach (var cuts in result.Values)
                cuts.Sort((x, y) => x.Parameter.CompareTo(y.Parameter));
            return result;
        }

        private static bool TryCrossing(Point2d a, Point2d b, Point2d c, Point2d d, out Point2d point)
        {
            point = Point2d.Origin;
            double ax = b.X - a.X, ay = b.Y - a.Y;
            double bx = d.X - c.X, by = d.Y - c.Y;
            double determinant = ax * by - ay * bx;
            if (Math.Abs(determinant) < MeshTol.ZeroSq) return false;
            double t = ((c.X - a.X) * by - (c.Y - a.Y) * bx) / determinant;
            double u = ((c.X - a.X) * ay - (c.Y - a.Y) * ax) / determinant;
            if (t < 0 || t > 1 || u < 0 || u > 1) return false;
            point = new Point2d(a.X + t * ax, a.Y + t * ay);
            return true;
        }

        private static void AddEndpoint(Dictionary<int, List<LocalSplitCut>> cuts,
            int index, Point2d a, Point2d b, Point2d point)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = a.GetDistanceTo(b);
            if (length <= MeshTol.NodeMerge) return;
            double distance = Math.Abs(dx * (point.Y - a.Y) - dy * (point.X - a.X)) / length;
            if (distance <= MeshTol.OnSegment) AddCut(cuts, index, a, b, point);
        }

        private static void AddCut(Dictionary<int, List<LocalSplitCut>> result,
            int index, Point2d a, Point2d b, Point2d point)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lengthSq = dx * dx + dy * dy;
            if (lengthSq <= MeshTol.ZeroSq) return;
            double t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSq;
            if (t <= 0 || t >= 1 || a.GetDistanceTo(point) <= MeshTol.NodeMerge
                || b.GetDistanceTo(point) <= MeshTol.NodeMerge) return;
            List<LocalSplitCut> cuts;
            if (!result.TryGetValue(index, out cuts))
            {
                cuts = new List<LocalSplitCut>();
                result.Add(index, cuts);
            }
            foreach (var existing in cuts)
                if (existing.Point.GetDistanceTo(point) <= MeshTol.NodeMerge) return;
            cuts.Add(new LocalSplitCut(t, point));
        }
    }
}
