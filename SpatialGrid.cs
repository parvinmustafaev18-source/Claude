using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
        // Пространственная сетка для поиска ближайших точек без перебора всех узлов на
        // каждый запрос: точки раскладываются по бакетам фиксированного размера, запрос
        // просматривает только бакеты, покрывающие нужный радиус. Без неё WeldShortNodes,
        // SplitSegmentsAtNodes, CloseOpenNodes и EnsureColumnCornerLinks были бы O(n²)
        // по числу узлов сетки, что на больших планах (тысячи сегментов) заметно тормозит.
        internal class SpatialGrid
        {
            private readonly double cellSize;
            private readonly Dictionary<long, List<int>> buckets =
                new Dictionary<long, List<int>>();

            public SpatialGrid(double cellSize)
            {
                this.cellSize = cellSize > 1e-9 ? cellSize : 1.0;
            }

            private static long Key(int cx, int cy)
            {
                return ((long)cx << 32) | (uint)cy;
            }

            private int CellOf(double v)
            {
                return (int)Math.Floor(v / cellSize);
            }

            public void Add(int index, Point2d p)
            {
                long key = Key(CellOf(p.X), CellOf(p.Y));
                List<int> list;
                if (!buckets.TryGetValue(key, out list))
                {
                    list = new List<int>();
                    buckets[key] = list;
                }
                list.Add(index);
            }

            public IEnumerable<int> QueryRadius(Point2d p, double radius)
            {
                int minCx = CellOf(p.X - radius), maxCx = CellOf(p.X + radius);
                int minCy = CellOf(p.Y - radius), maxCy = CellOf(p.Y + radius);

                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    for (int cy = minCy; cy <= maxCy; cy++)
                    {
                        List<int> list;
                        if (buckets.TryGetValue(Key(cx, cy), out list))
                        {
                            foreach (int idx in list) yield return idx;
                        }
                    }
                }
            }
        }

        // Индекс 2D-узлов по округлённым координатам (до 0.001 мм): совпадающие точки
        // получают один индекс. Новый узел всегда получает индекс Nodes.Count-1, поэтому
        // параллельный ему список (соседи, направления) наращивается проверкой
        // idx == list.Count сразу после GetNode.
        internal class NodeIndex
        {
            public readonly List<Point2d> Nodes = new List<Point2d>();
            private readonly Dictionary<string, int> index = new Dictionary<string, int>();

            public int GetNode(Point2d p)
            {
                string key = Math.Round(p.X, 3) + "_" + Math.Round(p.Y, 3);
                int idx;
                if (!index.TryGetValue(key, out idx))
                {
                    idx = Nodes.Count;
                    Nodes.Add(p);
                    index[key] = idx;
                }
                return idx;
            }
        }

}
