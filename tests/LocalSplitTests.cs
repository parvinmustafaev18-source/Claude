using Autodesk.AutoCAD.Geometry;
using MeshPlugin;
using System;
using System.Collections.Generic;

internal static class LocalSplitTests
{
    private static Point2d[] Edge(double x0, double y0, double x1, double y1)
    { return new[] { new Point2d(x0, y0), new Point2d(x1, y1) }; }

    private static void Check(string name, Point2d[][] input, int[] selected, int[] edges, double[][] parameters)
    {
        var segments = new List<Point2d[]>(input);
        var seeds = new HashSet<int>(selected);
        var result = LocalSplitCore.Calculate(segments, seeds);
        if (result.Count != edges.Length) throw new Exception(name + ": область изменений");
        for (int e = 0; e < edges.Length; e++)
        {
            List<LocalSplitCut> cuts;
            if (!result.TryGetValue(edges[e], out cuts) || cuts.Count != parameters[e].Length)
                throw new Exception(name + ": число разрезов");
            for (int c = 0; c < cuts.Count; c++)
                if (Math.Abs(cuts[c].Parameter - parameters[e][c]) > MeshTol.Zero)
                    throw new Exception(name + ": положение разреза");
        }
        // Не теряется ни один кусок, а существующие концы не перемещаются.
        var pieces = new List<Point2d[]>();
        var nextSeeds = new HashSet<int>();
        for (int i = 0; i < segments.Count; i++)
        {
            Point2d start = segments[i][0];
            double length = 0;
            List<LocalSplitCut> cuts;
            if (result.TryGetValue(i, out cuts))
                foreach (var cut in cuts)
                {
                    if (seeds.Contains(i)) nextSeeds.Add(pieces.Count);
                    pieces.Add(new[] { start, cut.Point });
                    length += start.GetDistanceTo(cut.Point);
                    start = cut.Point;
                }
            if (seeds.Contains(i)) nextSeeds.Add(pieces.Count);
            pieces.Add(new[] { start, segments[i][1] });
            length += start.GetDistanceTo(segments[i][1]);
            if (Math.Abs(length - segments[i][0].GetDistanceTo(segments[i][1])) > MeshTol.NodeMerge)
                throw new Exception(name + ": потеря геометрии");
        }
        if (LocalSplitCore.Calculate(pieces, nextSeeds).Count != 0)
            throw new Exception(name + ": повторный запуск меняет сетку");
        Console.WriteLine("PASS: " + name);
    }

    public static int Main()
    {
        try
        {
            Check("X / невыбранный сосед", new[] { Edge(0,0,10,0), Edge(5,-5,5,5) },
                new[] { 0 }, new[] { 0,1 }, new[] { new[] { .5 }, new[] { .5 } });
            Check("T", new[] { Edge(0,0,10,0), Edge(5,0,5,5) },
                new[] { 1 }, new[] { 0 }, new[] { new[] { .5 } });
            Check("общий конец", new[] { Edge(0,0,10,0), Edge(10,0,10,5) },
                new[] { 0 }, new int[0], new double[0][]);
            Check("локальность", new[] { Edge(0,0,10,0), Edge(5,-5,5,5), Edge(4,4,6,4), Edge(50,0,60,0) },
                new[] { 0 }, new[] { 0,1 }, new[] { new[] { .5 }, new[] { .5 } });
            Check("несколько разрезов", new[] { Edge(0,0,10,0), Edge(9,-5,9,5), Edge(3,-5,3,5), Edge(6,-5,6,5) },
                new[] { 0 }, new[] { 0,1,2,3 }, new[] { new[] { .3,.6,.9 }, new[] { .5 }, new[] { .5 }, new[] { .5 } });
            Check("тройное пересечение", new[] { Edge(0,0,10,0), Edge(5,-5,5,5), Edge(0,-5,10,5) },
                new[] { 0,1,2 }, new[] { 0,1,2 }, new[] { new[] { .5 }, new[] { .5 }, new[] { .5 } });
            Check("пересечение в 0.1 мм от конца", new[] { Edge(0,0,10,0), Edge(.1,-5,.1,5) },
                new[] { 0 }, new[] { 0,1 }, new[] { new[] { .01 }, new[] { .5 } });
            Check("конечный отрезок, не прямая", new[] { Edge(0,0,10,0), Edge(5,1,5,5) },
                new[] { 0 }, new int[0], new double[0][]);
            Check("коллинеарное примыкание", new[] { Edge(0,0,10,0), Edge(5,0,15,0) },
                new[] { 0 }, new[] { 0,1 }, new[] { new[] { .5 }, new[] { .5 } });
            Check("вырожденный отрезок", new[] { Edge(5,0,5,0), Edge(0,0,10,0) },
                new[] { 0 }, new[] { 1 }, new[] { new[] { .5 } });
            Check("пустой выбор", new[] { Edge(0,0,10,0), Edge(5,-5,5,5) },
                new int[0], new int[0], new double[0][]);
            // Сдвиг и поворот не меняют параметры разрезов.
            for (int i = 0; i < 100; i++)
            {
                double angle = i * Math.PI / 50;
                double dx = Math.Cos(angle), dy = Math.Sin(angle);
                double x = 1000000 + 37 * i, y = -1000000 + 23 * i;
                Check("поворот/сдвиг " + i, new[] { Edge(x,y,x+10*dx,y+10*dy),
                    Edge(x+5*dx+5*dy,y+5*dy-5*dx,x+5*dx-5*dy,y+5*dy+5*dx) },
                    new[] { 0 }, new[] { 0,1 }, new[] { new[] { .5 }, new[] { .5 } });
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
