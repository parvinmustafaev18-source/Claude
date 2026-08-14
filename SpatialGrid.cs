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

        // Индекс 2D-узлов: точки ближе допуска слияния получают ОДИН индекс.
        //
        // Раньше ключом была строка из округлённых координат. Округление режет
        // плоскость на клетки, и две точки, отличающиеся на тысячную миллиметра, но
        // лежащие по разные стороны границы клетки (1.0004999 и 1.0005001),
        // получали РАЗНЫЕ ключи и становились разными узлами. Сетка в таком месте не
        // смыкалась: узел числился «открытым» (круг ПРОБЛЕМА), а в ЛИРЕ на его месте
        // появлялась дыра. Координаты здесь почти всегда посчитанные (пересечения,
        // проекции, середины), так что попадание на границу клетки — обычное дело.
        //
        // Теперь точка ищется в 9 соседних бакетах и сливается с любым узлом ближе
        // допуска, поэтому граница бакета ничего не решает.
        //
        // Новый узел по-прежнему получает индекс Nodes.Count-1: параллельные списки
        // (соседи, направления) наращиваются проверкой idx == list.Count сразу
        // после GetNode.
        internal class NodeIndex
        {
            public readonly List<Point2d> Nodes = new List<Point2d>();
            private readonly Dictionary<long, List<int>> buckets =
                new Dictionary<long, List<int>>();
            private readonly double tol;
            private readonly double tolSq;

            public NodeIndex() : this(MeshTol.NodeMerge) { }

            public NodeIndex(double tolerance)
            {
                tol = tolerance > 0 ? tolerance : MeshTol.NodeMerge;
                tolSq = tol * tol;
            }

            private static long Key(int cx, int cy)
            {
                return ((long)cx << 32) | (uint)cy;
            }

            private int CellOf(double v)
            {
                return (int)Math.Floor(v / tol);
            }

            public int GetNode(Point2d p)
            {
                int cx = CellOf(p.X), cy = CellOf(p.Y);

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        List<int> found;
                        if (!buckets.TryGetValue(Key(cx + dx, cy + dy), out found)) continue;
                        foreach (int i in found)
                        {
                            double ex = Nodes[i].X - p.X, ey = Nodes[i].Y - p.Y;
                            if (ex * ex + ey * ey <= tolSq) return i;
                        }
                    }
                }

                int idx = Nodes.Count;
                Nodes.Add(p);

                long key = Key(cx, cy);
                List<int> list;
                if (!buckets.TryGetValue(key, out list))
                {
                    list = new List<int>();
                    buckets[key] = list;
                }
                list.Add(idx);
                return idx;
            }
        }

        // То же для 3D-узлов задачи ЛИРЫ: узлы плиты (z=0), стен и пилонов обязаны
        // совпасть в одну точку, иначе стена «проваливается» — стоит на своих узлах,
        // не связанных с плитой. Поиск идёт по 27 соседним бакетам.
        internal class NodeIndex3
        {
            // Ключ бакета — тройка целых. Упаковать её в long нельзя: при допуске
            // 0.001 мм координата 33 458 мм даёт номер бакета 33 458 000, и на три
            // оси 64 бит не хватает — номера начали бы накладываться друг на друга.
            private struct Cell3 : IEquatable<Cell3>
            {
                public readonly int X, Y, Z;
                public Cell3(int x, int y, int z) { X = x; Y = y; Z = z; }

                public bool Equals(Cell3 other)
                {
                    return X == other.X && Y == other.Y && Z == other.Z;
                }

                public override bool Equals(object obj)
                {
                    return obj is Cell3 && Equals((Cell3)obj);
                }

                public override int GetHashCode()
                {
                    unchecked
                    {
                        int h = X;
                        h = (h * 397) ^ Y;
                        h = (h * 397) ^ Z;
                        return h;
                    }
                }
            }

            public readonly List<double[]> Nodes = new List<double[]>();
            private readonly Dictionary<Cell3, List<int>> buckets =
                new Dictionary<Cell3, List<int>>();
            private readonly double tol;
            private readonly double tolSq;

            public NodeIndex3() : this(MeshTol.NodeMerge) { }

            public NodeIndex3(double tolerance)
            {
                tol = tolerance > 0 ? tolerance : MeshTol.NodeMerge;
                tolSq = tol * tol;
            }

            private int CellOf(double v)
            {
                return (int)Math.Floor(v / tol);
            }

            public int GetNode(double x, double y, double z)
            {
                int cx = CellOf(x), cy = CellOf(y), cz = CellOf(z);

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            List<int> found;
                            if (!buckets.TryGetValue(new Cell3(cx + dx, cy + dy, cz + dz), out found)) continue;
                            foreach (int i in found)
                            {
                                double ex = Nodes[i][0] - x, ey = Nodes[i][1] - y, ez = Nodes[i][2] - z;
                                if (ex * ex + ey * ey + ez * ez <= tolSq) return i;
                            }
                        }
                    }
                }

                int idx = Nodes.Count;
                Nodes.Add(new double[] { x, y, z });

                Cell3 key = new Cell3(cx, cy, cz);
                List<int> list;
                if (!buckets.TryGetValue(key, out list))
                {
                    list = new List<int>();
                    buckets[key] = list;
                }
                list.Add(idx);
                return idx;
            }
        }

}
