using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    public partial class Commands
    {
        private const double MinElementSize = 100.0;

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
        private List<Point2d> RemoveCollinearVertices(List<Point2d> poly, double eps = 0.5)
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
            if (sumSq < 1e-12) return 0.0;
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


        private string EdgeKey(Point2d a, Point2d b)
        {
            string ka = Math.Round(a.X, 3) + "_" + Math.Round(a.Y, 3);
            string kb = Math.Round(b.X, 3) + "_" + Math.Round(b.Y, 3);
            return string.CompareOrdinal(ka, kb) < 0 ? ka + "|" + kb : kb + "|" + ka;
        }

        private Point2d FindOppositeVertex(Point2d[] tri, Point2d a, Point2d b)
        {
            double tol = 1e-3;
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
            if (lenSq < 1e-12) return false;

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

        private void EnsureCcw(List<Point2d> poly)
        {
            if (PolygonArea(poly) < 0)
                poly.Reverse();
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

        private List<Point2d> CleanupPolygon(List<Point2d> poly, double eps = 1e-3)
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
                // Ушная триангуляция застряла (полигон почти вырожден или с дефектом) —
                // запасной вариант: веер от центроида, если все его треугольники корректны.
                // Иначе остаток полигона теряется, что учитывается в failedPolygons.
                double cx = 0, cy = 0;
                foreach (var p in verts) { cx += p.X; cy += p.Y; }
                Point2d c = new Point2d(cx / verts.Count, cy / verts.Count);

                var fan = new List<Point2d[]>();
                bool fanOk = true;
                int m = verts.Count;
                for (int i = 0; i < m; i++)
                {
                    Point2d a = verts[i];
                    Point2d b = verts[(i + 1) % m];
                    if (CrossProduct(a, b, c) <= 2e-3) { fanOk = false; break; }
                    fan.Add(new Point2d[] { a, b, c });
                }

                if (fanOk)
                    result.AddRange(fan);
                else
                    failedPolygons++;
            }

            return result;
        }

    }
}
