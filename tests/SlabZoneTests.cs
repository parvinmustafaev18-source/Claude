using Autodesk.AutoCAD.Geometry;
using MeshPlugin;
using System;
using System.Collections.Generic;

internal static class SlabZoneTests
{
    private static int checks;
    private static List<Point2d> Poly(params double[] xy)
    {
        var result = new List<Point2d>();
        for (int i = 0; i < xy.Length; i += 2) result.Add(new Point2d(xy[i], xy[i + 1]));
        return result;
    }
    private static List<Point2d[]> Edges(List<Point2d> polygon)
    {
        var result = new List<Point2d[]>();
        for (int i = 0; i < polygon.Count; i++) result.Add(new[] { polygon[i], polygon[(i + 1) % polygon.Count] });
        return result;
    }
    private static void Check(bool result, string name)
    {
        if (!result) throw new Exception(name);
        checks++;
    }
    private static double Thickness(List<Point2d> element, List<SlabThicknessZone> zones)
    {
        var index = new BboxIndex(MeshTol.MinElementSize);
        for (int i = 0; i < zones.Count; i++) index.AddPolygon(i, zones[i].Polygon);
        return SlabZoneCore.FindThickness(element, zones, index);
    }
    public static int Main()
    {
        try
        {
            var square = Poly(0, 0, 100, 0, 100, 100, 0, 100);
            var inside = Poly(10, 10, 20, 10, 20, 20, 10, 20);
            Check(SlabZoneCore.ContainsPolygon(inside, square), "quad inside");
            Check(SlabZoneCore.ContainsPolygon(Poly(0, 0, 100, 0, 0, 100), square), "triangle on boundary");
            Check(SlabZoneCore.ContainsPolygon(square, square), "coincident boundary");
            Check(!SlabZoneCore.ContainsPolygon(Poly(90, 20, 110, 20, 110, 40, 90, 40), square), "partial hit");
            Check(!SlabZoneCore.ContainsPolygon(Poly(-10, 0, 0, 0, 0, 10, -10, 10), square), "outside touch");
            // Узкая выемка: все вершины и середины рёбер элемента внутри, но часть
            // нижнего ребра проходит снаружи. Проверка по вершинам/середине ошиблась бы.
            var notch = Poly(0, 0, 20, 0, 20, 40, 30, 40, 30, 0, 100, 0, 100, 100, 0, 100);
            Check(!SlabZoneCore.ContainsPolygon(Poly(10, 10, 90, 10, 90, 80, 10, 80), notch), "narrow concave notch");
            Check(SlabZoneCore.ContainsPolygon(Poly(40, 10, 90, 10, 90, 80, 40, 80), notch), "concave inside");
            // Выход из контура через его вершины, без строгого Х-пересечения.
            var valley = Poly(0, 0, 20, 0, 20, 20, 30, 30, 40, 20, 40, 0, 100, 0, 100, 100, 0, 100);
            Check(!SlabZoneCore.ContainsPolygon(Poly(10, 20, 90, 20, 90, 80, 10, 80), valley), "exit via vertices");
            Check(SlabZoneCore.ValidatePolygon(notch, out _), "valid concave");
            Check(SlabZoneCore.ValidatePolygon(Poly(0, 0, 50, 0, 100, 0, 100, 100, 0, 100), out _), "collinear vertex");
            Check(!SlabZoneCore.ValidatePolygon(Poly(0, 0, 100, 100, 0, 100, 100, 0), out _), "self intersection");
            Check(!SlabZoneCore.ValidatePolygon(Poly(0, 0, 100, 0, 50, 0, 100, 100, 0, 100), out _), "backtracking");
            Check(!SlabZoneCore.ValidatePolygon(Poly(0, 0, 100, 0, 100, 100, 50, 0, 0, 100), out _), "self touch");
            Check(!SlabZoneCore.ValidatePolygon(Poly(0, 0, 0, 0, 100, 100), out _), "duplicate vertices");
            var edges = Edges(square);
            edges.RemoveAt(0);
            edges.Add(new[] { new Point2d(0, 0), new Point2d(50, 0) });
            edges.Add(new[] { new Point2d(100, 0), new Point2d(50, 0) });
            Check(SlabZoneCore.BoundaryCovered(square, edges, SlabZoneCore.IndexSegments(edges), out _), "chain in either direction");
            edges[edges.Count - 1] = new[] { new Point2d(100, 0), new Point2d(51, 0) };
            Check(!SlabZoneCore.BoundaryCovered(square, edges, SlabZoneCore.IndexSegments(edges), out int side) && side == 0, "gap on side 1");
            edges = Edges(square);
            edges[0] = new[] { new Point2d(0, 1), new Point2d(100, 1) };
            Check(!SlabZoneCore.BoundaryCovered(square, edges, SlabZoneCore.IndexSegments(edges), out _), "offset boundary");
            var zones = new List<SlabThicknessZone> { new SlabThicknessZone(square, 500, "A") };
            Check(Thickness(inside, zones) == 500, "zone thickness");
            Check(Thickness(Poly(90, 20, 110, 20, 110, 40, 90, 40), zones) == 0, "partial not assigned");
            Check(Thickness(inside, new List<SlabThicknessZone>()) == 0, "no zones");
            zones.Add(new SlabThicknessZone(square, 500, "B"));
            Check(Thickness(inside, zones) == 500, "equal overlaps");
            zones.Add(new SlabThicknessZone(square, 600, "C"));
            bool conflict = false;
            try { Thickness(inside, zones); } catch (InvalidOperationException) { conflict = true; }
            Check(conflict, "conflicting overlaps");
            square.Reverse();
            Check(SlabZoneCore.ContainsPolygon(inside, square), "clockwise zone");
            for (int pass = 0; pass < 100; pass++)
            {
                double angle = pass * Math.PI / 50.0, dx = 1000000 + pass * 113, dy = -2000000 - pass * 79;
                var movedZone = new List<Point2d>();
                var movedElement = new List<Point2d>();
                foreach (var p in notch) movedZone.Add(new Point2d(dx + p.X * Math.Cos(angle) - p.Y * Math.Sin(angle), dy + p.X * Math.Sin(angle) + p.Y * Math.Cos(angle)));
                foreach (var p in Poly(10, 10, 90, 10, 90, 80, 10, 80)) movedElement.Add(new Point2d(dx + p.X * Math.Cos(angle) - p.Y * Math.Sin(angle), dy + p.X * Math.Sin(angle) + p.Y * Math.Cos(angle)));
                Check(!SlabZoneCore.ContainsPolygon(movedElement, movedZone), "rotated notch " + pass);
                Check(SlabZoneCore.ValidatePolygon(movedZone, out _), "rotated valid " + pass);
            }
            Console.WriteLine("Slab zones: " + checks + " checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
