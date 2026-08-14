using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    public partial class Commands
    {
        // Значения допусков — в Defs.cs (MeshTol); здесь короткое привычное имя.
        private const double MinElementSize = MeshTol.MinElementSize;

        // Центры (центроиды вершин) сечений пилонов — узлы, к которым крепятся стержни.
        private List<Point2d> ComputeColumnCenters(List<List<Point2d>> columnPolys)
        {
            var centers = new List<Point2d>();
            foreach (var col in columnPolys)
            {
                double sx = 0, sy = 0;
                foreach (var p in col) { sx += p.X; sy += p.Y; }
                centers.Add(new Point2d(sx / col.Count, sy / col.Count));
            }
            return centers;
        }

        // Убирает вершины, лежащие на прямой между соседями (допуск 0.5 мм):
        // прямоугольник, начерченный с лишними промежуточными точками на сторонах,
        // снова становится четырёхвершинным.
        private List<Point2d> RemoveCollinearVertices(List<Point2d> poly, double eps = MeshTol.Collinear)
        {
            var result = new List<Point2d>(poly);
            bool removed = true;
            while (removed && result.Count > 3)
            {
                removed = false;
                for (int i = 0; i < result.Count; i++)
                {
                    Point2d prev = result[(i - 1 + result.Count) % result.Count];
                    Point2d next = result[(i + 1) % result.Count];
                    if (IsPointOnSegment(result[i], prev, next, eps))
                    {
                        result.RemoveAt(i);
                        removed = true;
                        break;
                    }
                }
            }
            return result;
        }

        // Качество формы элемента по методике ЛИРА-САПР (мозаика "Качество пластин"):
        // α ∈ [0;1], равносторонний треугольник/квадрат = 1, α < 0.5 — плохой элемент.
        private const double MinQualityAlpha = 0.5;

        // Треугольник: α = 4√3·S / (a² + b² + c²).
        private double TriangleAlpha(Point2d a, Point2d b, Point2d c)
        {
            double ab = a.GetDistanceTo(b), bc = b.GetDistanceTo(c), ca = c.GetDistanceTo(a);
            double sumSq = ab * ab + bc * bc + ca * ca;
            if (sumSq < MeshTol.ZeroSq) return 0.0;
            double area = Math.Abs(CrossProduct(a, b, c)) / 2.0;
            return 4.0 * Math.Sqrt(3.0) * area / sumSq;
        }

        // Четырёхугольник ABCD: α четырёх накладывающихся треугольников
        // {ABC, ACD, ABD, BCD}, итог — худшее из отношения произведений
        // противолежащих пар и отклонения среднего от √3/2 (α прямоугольного
        // равнобедренного треугольника, т.е. половины квадрата).
        private double QuadAlpha(Point2d[] q)
        {
            double a1 = TriangleAlpha(q[0], q[1], q[2]);
            double a2 = TriangleAlpha(q[0], q[2], q[3]);
            double a3 = TriangleAlpha(q[0], q[1], q[3]);
            double a4 = TriangleAlpha(q[1], q[2], q[3]);

            double t1 = a1 * a3, t2 = a2 * a4;
            if (t1 < 1e-12 || t2 < 1e-12) return 0.0;
            double alpha = t1 > t2 ? t2 / t1 : t1 / t2;

            const double rect = 0.8660254; // √3/2
            double avg = (rect - Math.Abs((a1 + a2 + a3 + a4) / 4.0 - rect)) / rect;

            return Math.Min(alpha, avg);
        }

        private bool QuadShapeOk(Point2d[] quad)
        {
            return QuadAlpha(quad) >= MinQualityAlpha;
        }


        // Ключ ребра по индексам его узлов (порядок концов не важен). Индексы
        // выдаёт NodeIndex, который сливает точки по допуску, — поэтому ребро
        // опознаётся как то же самое даже при разнице координат в тысячные доли мм.
        // Прежний вариант строил ключ из округлённых координат и на границе
        // округления считал одно и то же ребро двумя разными.
        private static long EdgePairKey(int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }

        private Point2d FindOppositeVertex(Point2d[] tri, Point2d a, Point2d b)
        {
            double tol = MeshTol.NodeMerge;
            foreach (var v in tri)
            {
                bool closeToA = Math.Abs(v.X - a.X) < tol && Math.Abs(v.Y - a.Y) < tol;
                bool closeToB = Math.Abs(v.X - b.X) < tol && Math.Abs(v.Y - b.Y) < tol;

                if (!closeToA && !closeToB)
                    return v;
            }
            return tri[0];
        }

        private bool IsConvexQuad(Point2d[] quad)
        {
            int sign = 0;
            for (int i = 0; i < 4; i++)
            {
                Point2d p0 = quad[i];
                Point2d p1 = quad[(i + 1) % 4];
                Point2d p2 = quad[(i + 2) % 4];

                double cross = CrossProduct(p0, p1, p2);
                int s = cross > 0 ? 1 : (cross < 0 ? -1 : 0);

                if (s == 0) continue;

                if (sign == 0) sign = s;
                else if (s != sign) return false;
            }
            return true;
        }


        private bool IsPointOnSegment(Point2d p, Point2d a, Point2d b, double eps)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < MeshTol.ZeroSq) return false;

            double len = Math.Sqrt(lenSq);
            double dist = Math.Abs(CrossProduct(a, b, p)) / len;
            if (dist > eps) return false;

            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
            double tolT = eps / len;
            return t >= -tolT && t <= 1 + tolT;
        }


        private bool IsPointInPolygon(Point2d point, List<Point2d> polygon)
        {
            bool inside = false;
            int n = polygon.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Point2d pi = polygon[i];
                Point2d pj = polygon[j];

                bool edgeCrosses = ((pi.Y > point.Y) != (pj.Y > point.Y)) &&
                    (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X);

                if (edgeCrosses)
                    inside = !inside;
            }
            return inside;
        }

        private double CrossProduct(Point2d a, Point2d b, Point2d c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        private bool SegmentsIntersect(Point2d p1, Point2d p2, Point2d p3, Point2d p4)
        {
            double d1 = CrossProduct(p3, p4, p1);
            double d2 = CrossProduct(p3, p4, p2);
            double d3 = CrossProduct(p1, p2, p3);
            double d4 = CrossProduct(p1, p2, p4);

            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                   ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        // Полигон целиком в пределах контура: каждая вершина внутри или на границе,
        // стороны не пересекают стороны контура, середина каждой стороны внутри
        // (ловит выход наружу через выемку вогнутого контура при вершинах на границе).
        // Касание границы — допустимо, выход наружу — нет.
        private bool IsPolygonInsideContour(List<Point2d> poly, List<Point2d> contour)
        {
            int n = poly.Count, cn = contour.Count;

            bool InsideOrOnBoundary(Point2d p)
            {
                for (int k = 0; k < cn; k++)
                    if (IsPointOnSegment(p, contour[k], contour[(k + 1) % cn], MeshTol.OnSegment)) return true;
                return IsPointInPolygon(p, contour);
            }

            for (int i = 0; i < n; i++)
            {
                Point2d a = poly[i];
                Point2d b = poly[(i + 1) % n];

                if (!InsideOrOnBoundary(a)) return false;

                for (int k = 0; k < cn; k++)
                    if (SegmentsIntersect(a, b, contour[k], contour[(k + 1) % cn])) return false;

                Point2d mid = new Point2d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
                if (!InsideOrOnBoundary(mid)) return false;
            }
            return true;
        }

        // Отрезок целиком в пределах контура: оба конца и середина внутри или на
        // границе, пересечений со сторонами контура нет. Касание границы допустимо.
        private bool IsSegmentInsideContour(Point2d a, Point2d b, List<Point2d> contour)
        {
            int cn = contour.Count;

            bool InsideOrOnBoundary(Point2d p)
            {
                for (int k = 0; k < cn; k++)
                    if (IsPointOnSegment(p, contour[k], contour[(k + 1) % cn], MeshTol.OnSegment)) return true;
                return IsPointInPolygon(p, contour);
            }

            if (!InsideOrOnBoundary(a) || !InsideOrOnBoundary(b)) return false;

            for (int k = 0; k < cn; k++)
                if (SegmentsIntersect(a, b, contour[k], contour[(k + 1) % cn])) return false;

            Point2d mid = new Point2d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
            return InsideOrOnBoundary(mid);
        }

        private bool IsCellFullyInside(Point2d[] cell, List<Point2d> contour)
        {
            foreach (var corner in cell)
            {
                if (!IsPointInPolygon(corner, contour))
                    return false;
            }

            int n = contour.Count;
            for (int i = 0; i < n; i++)
            {
                Point2d c1 = contour[i];
                Point2d c2 = contour[(i + 1) % n];

                for (int k = 0; k < 4; k++)
                {
                    Point2d s1 = cell[k];
                    Point2d s2 = cell[(k + 1) % 4];

                    if (SegmentsIntersect(c1, c2, s1, s2))
                        return false;
                }
            }

            return true;
        }

        private double PolygonArea(List<Point2d> poly)
        {
            double area = 0.0;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                Point2d a = poly[i];
                Point2d b = poly[(i + 1) % n];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area / 2.0;
        }

        // Среднее вершин: для маркеров проблемных мест точности достаточно.
        private Point2d PolygonCentroid(List<Point2d> poly)
        {
            double sx = 0, sy = 0;
            foreach (var p in poly) { sx += p.X; sy += p.Y; }
            return new Point2d(sx / poly.Count, sy / poly.Count);
        }

        private void EnsureCcw(List<Point2d> poly)
        {
            if (PolygonArea(poly) < 0)
                poly.Reverse();
        }

        // Точки самопересечения замкнутого контура — все пары несмежных сторон.
        // При наложении параллельных сторон точки пересечения нет, берётся середина
        // второй стороны. Общая для ValidateContour (останавливает построение на
        // первой) и MESHCHECK (показывает все сразу).
        private List<Point2d> FindSelfIntersections(List<Point2d> poly)
        {
            var result = new List<Point2d>();
            int m = poly.Count;
            for (int i = 0; i < m; i++)
            {
                for (int j = i + 1; j < m; j++)
                {
                    if (j == i + 1 || (i == 0 && j == m - 1)) continue; // смежные стороны

                    Point2d p1 = poly[i], p2 = poly[(i + 1) % m], p3 = poly[j], p4 = poly[(j + 1) % m];
                    if (!SegmentsIntersect(p1, p2, p3, p4)) continue;

                    double denom = (p2.X - p1.X) * (p4.Y - p3.Y) - (p2.Y - p1.Y) * (p4.X - p3.X);
                    if (Math.Abs(denom) < MeshTol.Zero)
                    {
                        result.Add(new Point2d((p3.X + p4.X) / 2.0, (p3.Y + p4.Y) / 2.0));
                        continue;
                    }

                    double t = ((p3.X - p1.X) * (p4.Y - p3.Y) - (p3.Y - p1.Y) * (p4.X - p3.X)) / denom;
                    result.Add(new Point2d(p1.X + (p2.X - p1.X) * t, p1.Y + (p2.Y - p1.Y) * t));
                }
            }
            return result;
        }

        // Вершины контура, где стороны не перпендикулярны (отклонение от 90° больше
        // 0.5°). Вершина на прямом участке (угол ≈ 180°) углом не считается — это
        // промежуточная точка стороны. Кривой угол не ошибка (бывает кривая
        // подоснова), но сетка у него заметно хуже, поэтому места возвращаются
        // вместе с углами — и для предупреждения, и для маркеров на чертеже.
        private List<int> FindNonRightCorners(List<Point2d> poly, out List<double> angles)
        {
            var result = new List<int>();
            angles = new List<double>();

            int m = poly.Count;
            for (int i = 0; i < m; i++)
            {
                Point2d prev = poly[(i - 1 + m) % m], cur = poly[i], next = poly[(i + 1) % m];
                double l1 = prev.GetDistanceTo(cur), l2 = cur.GetDistanceTo(next);
                if (l1 < MeshTol.Zero || l2 < MeshTol.Zero) continue;

                double dot = ((cur.X - prev.X) * (next.X - cur.X) + (cur.Y - prev.Y) * (next.Y - cur.Y)) / (l1 * l2);
                dot = Math.Max(-1.0, Math.Min(1.0, dot));
                double angle = 180.0 - Math.Acos(dot) * 180.0 / Math.PI;

                if (angle > 175.0) continue; // промежуточная точка на прямой стороне
                if (Math.Abs(angle - 90.0) <= 0.5) continue;

                result.Add(i);
                angles.Add(angle);
            }
            return result;
        }

        private Point2d LineIntersection(Point2d p1, Point2d p2, Point2d clipA, Point2d clipB)
        {
            double d1 = CrossProduct(clipA, clipB, p1);
            double d2 = CrossProduct(clipA, clipB, p2);
            double denom = d1 - d2;
            if (Math.Abs(denom) < 1e-9)
                denom = (denom >= 0 ? 1e-9 : -1e-9);
            double t = d1 / denom;
            return new Point2d(p1.X + t * (p2.X - p1.X), p1.Y + t * (p2.Y - p1.Y));
        }

        private List<Point2d> ClipPolygonAgainstEdge(List<Point2d> subject, Point2d clipA, Point2d clipB)
        {
            var output = new List<Point2d>();
            int n = subject.Count;
            if (n == 0) return output;

            for (int i = 0; i < n; i++)
            {
                Point2d current = subject[i];
                Point2d prev = subject[(i - 1 + n) % n];

                bool currentInside = CrossProduct(clipA, clipB, current) >= 0;
                bool prevInside = CrossProduct(clipA, clipB, prev) >= 0;

                if (currentInside)
                {
                    if (!prevInside)
                        output.Add(LineIntersection(prev, current, clipA, clipB));
                    output.Add(current);
                }
                else if (prevInside)
                {
                    output.Add(LineIntersection(prev, current, clipA, clipB));
                }
            }

            return output;
        }

        private List<Point2d> ClipPolygonToConvexCell(List<Point2d> subject, Point2d[] cell)
        {
            var result = new List<Point2d>(subject);
            int n = cell.Length;
            for (int i = 0; i < n && result.Count > 0; i++)
            {
                result = ClipPolygonAgainstEdge(result, cell[i], cell[(i + 1) % n]);
            }
            return result;
        }

        private List<Point2d> CleanupPolygon(List<Point2d> poly, double eps = MeshTol.OnSegment)
        {
            var result = new List<Point2d>();
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                Point2d p = poly[i];
                if (result.Count > 0)
                {
                    Point2d last = result[result.Count - 1];
                    if (Math.Abs(p.X - last.X) < eps && Math.Abs(p.Y - last.Y) < eps)
                        continue;
                }
                result.Add(p);
            }

            if (result.Count > 1)
            {
                Point2d first = result[0];
                Point2d last = result[result.Count - 1];
                if (Math.Abs(first.X - last.X) < eps && Math.Abs(first.Y - last.Y) < eps)
                    result.RemoveAt(result.Count - 1);
            }

            return result;
        }

        private bool IsPointInTriangle(Point2d p, Point2d a, Point2d b, Point2d c)
        {
            double d1 = CrossProduct(a, b, p);
            double d2 = CrossProduct(b, c, p);
            double d3 = CrossProduct(c, a, p);

            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;

            return !(hasNeg && hasPos);
        }

        private List<Point2d[]> TriangulateSimplePolygon(
            List<Point2d> poly,
            ref int failedPolygons)
        {
            var result = new List<Point2d[]>();
            var verts = new List<Point2d>(poly);

            if (verts.Count < 3) return result;
            if (PolygonArea(verts) < 0) verts.Reverse();

            int guard = 0;
            while (verts.Count > 3 && guard < 1000)
            {
                guard++;
                bool earFound = false;
                int n = verts.Count;

                for (int i = 0; i < n; i++)
                {
                    Point2d prev = verts[(i - 1 + n) % n];
                    Point2d cur = verts[i];
                    Point2d next = verts[(i + 1) % n];

                    if (CrossProduct(prev, cur, next) <= 0) continue;

                    bool anyInside = false;
                    for (int k = 0; k < n; k++)
                    {
                        if (k == (i - 1 + n) % n || k == i || k == (i + 1) % n) continue;
                        if (IsPointInTriangle(verts[k], prev, cur, next)) { anyInside = true; break; }
                    }
                    if (anyInside) continue;

                    result.Add(new Point2d[] { prev, cur, next });
                    verts.RemoveAt(i);
                    earFound = true;
                    break;
                }

                if (!earFound) break;
            }

            if (verts.Count == 3)
            {
                result.Add(new Point2d[] { verts[0], verts[1], verts[2] });
            }
            else if (verts.Count > 3)
            {
                // Ушная триангуляция застряла. Типовой случай — висячий узел на прямом
                // участке границы: вершина коллинеарна соседям, ухо в ней вырождено, а
                // выбрасывать её нельзя (узел обязан остаться углом треугольников, иначе
                // в ЛИРЕ он не связан с пластиной). Запасной вариант: рекурсивное
                // разрезание полигона по внутренней диагонали — работает и с
                // коллинеарными вершинами. Полный провал учитывается в failedPolygons.
                var rest = new List<Point2d[]>();
                if (TriangulateByDiagonalSplit(verts, rest, 0))
                    result.AddRange(rest);
                else
                    failedPolygons++;
            }

            return result;
        }

        // Разрезание простого полигона по внутренней диагонали, рекурсивно: диагональ
        // не пересекает стороны и её середина внутри полигона; каждая часть разбирается
        // так же, вплоть до треугольников (нулевые по площади пропускаются).
        private bool TriangulateByDiagonalSplit(List<Point2d> verts, List<Point2d[]> result, int depth)
        {
            int n = verts.Count;
            if (n < 3) return true;
            if (n == 3)
            {
                if (Math.Abs(CrossProduct(verts[0], verts[1], verts[2])) > 1e-6)
                    result.Add(new Point2d[] { verts[0], verts[1], verts[2] });
                return true;
            }
            if (depth > 64) return false;

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue; // смежные вершины

                    Point2d a = verts[i], b = verts[j];
                    if (a.GetDistanceTo(b) < 1e-6) continue;

                    bool bad = false;
                    for (int k = 0; k < n && !bad; k++)
                        if (SegmentsIntersect(a, b, verts[k], verts[(k + 1) % n])) bad = true;
                    if (bad) continue;

                    Point2d mid = new Point2d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
                    if (!IsPointInPolygon(mid, verts)) continue;

                    var left = new List<Point2d>();
                    for (int k = i; k != (j + 1) % n; k = (k + 1) % n) left.Add(verts[k]);
                    var right = new List<Point2d>();
                    for (int k = j; k != (i + 1) % n; k = (k + 1) % n) right.Add(verts[k]);
                    if (left.Count < 3 || right.Count < 3) continue;

                    var sub = new List<Point2d[]>();
                    if (TriangulateByDiagonalSplit(left, sub, depth + 1) &&
                        TriangulateByDiagonalSplit(right, sub, depth + 1))
                    {
                        result.AddRange(sub);
                        return true;
                    }
                }
            }
            return false;
        }

    }
}
