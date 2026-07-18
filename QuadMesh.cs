using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    public partial class Commands
    {
        [CommandMethod("MESHQUADMESH")]
        public void HybridMeshCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            PromptEntityOptions peo = new PromptEntityOptions("\nВыберите замкнутый контур (полилинию): ");
            peo.SetRejectMessage("\nМожно выбрать только полилинию (LWPOLYLINE).");
            peo.AddAllowedClass(typeof(Polyline), false);

            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён или не удался.\n");
                return;
            }

            PromptDoubleOptions pdoSize = new PromptDoubleOptions("\nРазмер стороны элемента сетки: ");
            pdoSize.DefaultValue = 300.0;
            pdoSize.AllowNegative = false;
            pdoSize.AllowZero = false;
            pdoSize.Keywords.Add("300");
            pdoSize.Keywords.Add("400");
            pdoSize.Keywords.Add("500");
            pdoSize.AppendKeywordsToMessage = true;

            PromptDoubleResult pdrSize = ed.GetDouble(pdoSize);

            double cellSize;
            if (pdrSize.Status == PromptStatus.Keyword)
            {
                cellSize = double.Parse(pdrSize.StringResult);
            }
            else if (pdrSize.Status == PromptStatus.OK)
            {
                cellSize = pdrSize.Value;
            }
            else
            {
                ed.WriteMessage("\nВвод отменён.\n");
                return;
            }

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Polyline pline = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Polyline;

                if (pline == null)
                {
                    ed.WriteMessage("\nНужен замкнутый контур (полилиния).\n");
                    return;
                }

                if (!ValidateContour(pline, ed, tr, db, out var contourPts)) return;
                EnsureCcw(contourPts);

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var p in contourPts)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }

                int snappedWalls = SnapWallsToGrid(tr, db, minX, minY, cellSize);
                var wallSegments = GetWallSegments(tr, db);
                ed.WriteMessage($"\nНайдено сегментов стен: {wallSegments.Count}, подвинуто к узлам сетки (до {WallSnapTolerance:0} мм): {snappedWalls}\n");

                // Пилоны (слой COLUMNS): контур пилона врезается в сетку как стены,
                // внутри пилона строится своя сетка с шагом ColumnMeshStep в двух направлениях.
                int snappedColumns = SnapColumnsToGrid(tr, db, minX, minY, cellSize);
                var columnPolys = GetColumnPolygons(tr, db);
                var cutSegments = new List<Point2d[]>(wallSegments);
                foreach (var col in columnPolys)
                {
                    int cn = col.Count;
                    for (int i = 0; i < cn; i++)
                        cutSegments.Add(new Point2d[] { col[i], col[(i + 1) % cn] });
                }
                ed.WriteMessage($"\nНайдено пилонов: {columnPolys.Count}, подвинуто к сетке (до {WallSnapTolerance:0} мм): {snappedColumns}\n");

                var quadCells = new List<Point2d[]>();
                var boundaryCells = new List<Point2d[]>();
                var wallCells = new List<Point2d[]>();

                // Линии сетки допускается смещать к граням пилонов для чистоты разбиения
                // (увеличение ячейки ≤30% шага, но не более 100 мм).
                var colXs = new List<double>();
                var colYs = new List<double>();
                foreach (var col in columnPolys)
                {
                    foreach (var p in col)
                    {
                        colXs.Add(p.X);
                        colYs.Add(p.Y);
                    }
                }

                var xs = BuildGridCoords(minX, maxX, cellSize, colXs, out int shiftedX);
                var ys = BuildGridCoords(minY, maxY, cellSize, colYs, out int shiftedY);
                if (shiftedX + shiftedY > 0)
                    ed.WriteMessage($"\nЛиний сетки смещено к граням пилонов: {shiftedX + shiftedY}\n");

                for (int xi = 0; xi + 1 < xs.Count; xi++)
                {
                    for (int yi = 0; yi + 1 < ys.Count; yi++)
                    {
                        Point2d[] cell = new Point2d[]
                        {
                            new Point2d(xs[xi], ys[yi]),
                            new Point2d(xs[xi + 1], ys[yi]),
                            new Point2d(xs[xi + 1], ys[yi + 1]),
                            new Point2d(xs[xi], ys[yi + 1])
                        };

                        if (CellInsideAnyColumn(cell, columnPolys))
                        {
                            continue; // внутри пилона сетка плиты не нужна — там своя, 100 мм
                        }

                        if (CellTouchesWalls(cell, cutSegments))
                        {
                            wallCells.Add(cell);
                        }
                        else if (IsCellFullyInside(cell, contourPts))
                        {
                            quadCells.Add(cell);
                        }
                        else
                        {
                            boundaryCells.Add(cell);
                        }
                    }
                }

                ed.WriteMessage($"\nПостроено квадратных элементов: {quadCells.Count}, ячеек у стен: {wallCells.Count}\n");

                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                var allSegments = new List<Point2d[]>();

                foreach (var cell in quadCells)
                {
                    AddQuadSegments(allSegments, cell);
                }

                // Кайма вдоль границы: каждую неполную ячейку сетки обрезаем по контуру
                // напрямую (Sutherland-Hodgman), без триангуляции всей полосы.
                var triVerts = new List<Point2d[]>();
                var directQuads = new List<Point2d[]>();
                int failedPolygons = 0;

                foreach (var cell in boundaryCells)
                {
                    var clipped = ClipPolygonToConvexCell(contourPts, cell);
                    clipped = CleanupPolygon(clipped);

                    if (clipped.Count < 3) continue;
                    if (Math.Abs(PolygonArea(clipped)) < 1e-3) continue;

                    if (clipped.Count == 4 && IsConvexQuad(clipped.ToArray()))
                    {
                        directQuads.Add(clipped.ToArray());
                    }
                    else
                    {
                        foreach (var tri in TriangulateSimplePolygon(clipped, ref failedPolygons))
                        {
                            if (Math.Abs(PolygonArea(new List<Point2d>(tri))) < 1e-3) continue;
                            triVerts.Add(tri);
                        }
                    }
                }

                // Ячейки, задетые стеной: ячейка разрезается по линии стены на части
                // (Sutherland-Hodgman по обеим полуплоскостям) — стена становится рёбрами
                // сетки с общими узлами, элементы у стены уменьшаются, но не более чем вдвое.
                foreach (var cell in wallCells)
                {
                    var clipped = ClipPolygonToConvexCell(contourPts, cell);
                    clipped = CleanupPolygon(clipped);

                    if (clipped.Count < 3) continue;
                    if (Math.Abs(PolygonArea(clipped)) < 1e-3) continue;

                    foreach (var piece in SplitPolygonByWalls(clipped, cutSegments))
                    {
                        if (piece.Count < 3) continue;
                        if (Math.Abs(PolygonArea(piece)) < 1e-3) continue;
                        if (PieceInsideAnyColumn(piece, columnPolys)) continue;

                        if (piece.Count == 4 && IsConvexQuad(piece.ToArray()))
                        {
                            directQuads.Add(piece.ToArray());
                        }
                        else
                        {
                            foreach (var tri in TriangulateSimplePolygon(piece, ref failedPolygons))
                            {
                                if (Math.Abs(PolygonArea(new List<Point2d>(tri))) < 1e-3) continue;
                                triVerts.Add(tri);
                            }
                        }
                    }
                }

                foreach (var quad in directQuads)
                {
                    AddQuadSegments(allSegments, quad);
                }

                ed.WriteMessage($"\nТреугольников по краю (до объединения): {triVerts.Count}\n");

                // Справочник "сторона -> какие треугольники её используют"
                var edgeMap = new Dictionary<string, List<int>>();

                for (int i = 0; i < triVerts.Count; i++)
                {
                    var t = triVerts[i];
                    string[] keys = new string[]
                    {
                        EdgeKey(t[0], t[1]),
                        EdgeKey(t[1], t[2]),
                        EdgeKey(t[2], t[0])
                    };

                    foreach (var k in keys)
                    {
                        if (!edgeMap.ContainsKey(k))
                            edgeMap[k] = new List<int>();
                        edgeMap[k].Add(i);
                    }
                }

                // Жадное объединение пар треугольников в четырёхугольники
                bool[] used = new bool[triVerts.Count];
                var mergedQuads = new List<Point2d[]>();

                for (int i = 0; i < triVerts.Count; i++)
                {
                    if (used[i]) continue;

                    var t = triVerts[i];
                    string[] keys = new string[]
                    {
                        EdgeKey(t[0], t[1]),
                        EdgeKey(t[1], t[2]),
                        EdgeKey(t[2], t[0])
                    };
                    Point2d[] edgeStart = new Point2d[] { t[0], t[1], t[2] };
                    Point2d[] edgeEnd = new Point2d[] { t[1], t[2], t[0] };
                    Point2d[] opposite = new Point2d[] { t[2], t[0], t[1] };

                    bool merged = false;

                    for (int side = 0; side < 3 && !merged; side++)
                    {
                        // Правило ЛИРЫ: не объединять, если через общую сторону проходят
                        // другие элементы — ребро должно принадлежать ровно двум
                        // треугольникам и не лежать на стене/грани пилона.
                        if (edgeMap[keys[side]].Count != 2) continue;

                        Point2d edgeMid = new Point2d(
                            (edgeStart[side].X + edgeEnd[side].X) / 2.0,
                            (edgeStart[side].Y + edgeEnd[side].Y) / 2.0);
                        bool onWall = false;
                        foreach (var w in cutSegments)
                            if (IsPointOnSegment(edgeMid, w[0], w[1], 1e-3)) { onWall = true; break; }
                        if (onWall) continue;

                        foreach (int j in edgeMap[keys[side]])
                        {
                            if (j == i || used[j]) continue;

                            Point2d d = FindOppositeVertex(triVerts[j], edgeStart[side], edgeEnd[side]);
                            Point2d a = edgeStart[side];
                            Point2d b = edgeEnd[side];
                            Point2d c = opposite[side];

                            Point2d[] quad = new Point2d[] { a, c, b, d };

                            // Сливать только если четырёхугольник не вырожден по углам:
                            // выпуклый, но игольчатый квад хуже двух нормальных треугольников.
                            if (IsConvexQuad(quad) && QuadShapeOk(quad))
                            {
                                mergedQuads.Add(quad);
                                used[i] = true;
                                used[j] = true;
                                merged = true;
                                break;
                            }
                        }
                    }
                }

                int leftoverCount = 0;

                foreach (var quad in mergedQuads)
                {
                    AddQuadSegments(allSegments, quad);
                }

                for (int i = 0; i < triVerts.Count; i++)
                {
                    if (used[i]) continue;
                    leftoverCount++;

                    AddTriSegments(allSegments, triVerts[i]);
                }

                ed.WriteMessage($"\nПрямых четырёхугольников по краю: {directQuads.Count}, получено четырёхугольников из объединения: {mergedQuads.Count}, осталось одиночных треугольников: {leftoverCount}\n");

                // Контроль качества: дальше элементы превращаются в отрезки и их форма
                // теряется, поэтому вырожденные элементы пересчитываются здесь.
                if (failedPolygons > 0)
                    ed.WriteMessage($"\nВНИМАНИЕ: не удалось триангулировать полигонов: {failedPolygons} — возможны дыры в сетке по краю или у стен.\n");

                int poorTris = 0, poorQuads = 0;
                double worstAlpha = 1.0;
                for (int i = 0; i < triVerts.Count; i++)
                {
                    if (used[i]) continue;
                    double alpha = TriangleAlpha(triVerts[i][0], triVerts[i][1], triVerts[i][2]);
                    if (alpha < worstAlpha) worstAlpha = alpha;
                    if (alpha < MinQualityAlpha) poorTris++;
                }
                foreach (var q in directQuads)
                {
                    double alpha = QuadAlpha(q);
                    if (alpha < worstAlpha) worstAlpha = alpha;
                    if (alpha < MinQualityAlpha) poorQuads++;
                }
                if (poorTris + poorQuads > 0)
                    ed.WriteMessage($"\nВНИМАНИЕ: элементов с качеством α<{MinQualityAlpha:0.0#} (по методике ЛИРА-САПР): треугольников: {poorTris}, четырёхугольников: {poorQuads}, худший α={worstAlpha:0.00}\n");

                var uniqueSegments = DeduplicateSegments(allSegments);
                var innerSegments = RemoveSegmentsOnContour(uniqueSegments, contourPts, out int removedOnContour);
                innerSegments = ResolveOverlappingSegments(innerSegments, cutSegments, out int removedOnWalls, out int mergedOverlaps);

                // Рёбра короче MinElementSize (100 мм) недопустимы: подвижные узлы сетки
                // смещаются к неподвижной геометрии или сливаются друг с другом.
                innerSegments = WeldShortNodes(innerSegments, wallSegments, columnPolys, contourPts, out int weldedEdges);
                if (weldedEdges > 0)
                {
                    innerSegments = DeduplicateSegments(innerSegments);
                    innerSegments = RemoveSegmentsOnContour(innerSegments, contourPts, out _);
                    innerSegments = ResolveOverlappingSegments(innerSegments, cutSegments, out _, out _);
                }

                // Каждый угол пилона обязан быть связан с сеткой минимум в двух направлениях.
                innerSegments = EnsureColumnCornerLinks(innerSegments, columnPolys, cellSize, out int cornerLinks);

                // Линия не может обрываться посреди другого элемента: узел, лежащий
                // внутри чужого отрезка, делит его на два (общий узел для обоих).
                innerSegments = SplitSegmentsAtNodes(innerSegments, cellSize, out int splitEdges);

                // Открытые узлы недопустимы: точка, упершаяся в линию, замыкается
                // наклонной в соседний узел (угол по возможности близок к 30/45°).
                innerSegments = CloseOpenNodes(innerSegments, cutSegments, contourPts, columnPolys, cellSize, out int closedNodes);
                if (closedNodes > 0)
                    innerSegments = SplitSegmentsAtNodes(innerSegments, cellSize, out _);

                // Финальный шаг: сглаживание подвижных узлов для повышения качества α.
                innerSegments = SmoothMesh(innerSegments, cutSegments, contourPts, columnPolys, xs, ys, out int smoothedNodes);
                if (smoothedNodes > 0)
                    ed.WriteMessage($"\nСглажено узлов (Лаплас): {smoothedNodes}\n");

                // Жёсткое правило: перед отрисовкой отсекается всё, что пересекает
                // контур плиты или лежит вне его — независимо от того, какой из
                // предыдущих шагов построил такой отрезок.
                innerSegments = EnforceInsideContour(innerSegments, contourPts, out int removedOutside);
                if (removedOutside > 0)
                    ed.WriteMessage($"\nВНИМАНИЕ: отсечено отрезков, выходивших за контур плиты: {removedOutside}\n");

                foreach (var seg in innerSegments)
                    DrawSegment(btr, tr, seg[0], seg[1]);

                // Контур пилона разбивается на отрезки и переносится в слой линий
                // триангуляции (полилиния удаляется, точка центра остаётся в COLUMNS).
                int explodedColumns = ExplodeColumnContours(tr, db);
                if (explodedColumns > 0)
                    ed.WriteMessage($"\nКонтуров пилонов разбито на отрезки в {TriangulationLayerName}: {explodedColumns}\n");

                ed.WriteMessage($"\nОтрезков всего: {allSegments.Count}, после удаления совпадающих: {uniqueSegments.Count}, удалено по внешнему контуру: {removedOnContour}, срезано по стенам: {removedOnWalls}, устранено наложений: {mergedOverlaps}, схлопнуто коротких рёбер: {weldedEdges}, связей углов пилонов: {cornerLinks}, разбито рёбер узлами: {splitEdges}, замкнуто открытых узлов: {closedNodes}, итог: {innerSegments.Count}\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                // Транзакция не закоммичена — все изменения команды откатились.
                ed.WriteMessage($"\nОшибка MESHQUADMESH: {ex.Message}\nИзменения команды отменены.\n");
            }
        }


        // Сглаживание по Лапласу: подвижный узел смещается к среднему своих соседей —
        // поднимает качество α вытянутых элементов по краю. Неподвижны: узлы на контуре
        // плиты, стенах, гранях/центрах пилонов и узлы в пересечениях основной сетки
        // (их сдвиг портил бы правильные квадраты). Смещение отменяется, если узел
        // выходит из контура, попадает в пилон, создаёт ребро короче MinElementSize
        // или ребро, пересекающее стену/контур.
        private List<Point2d[]> SmoothMesh(
            List<Point2d[]> segments,
            List<Point2d[]> cutSegments,
            List<Point2d> contourPts,
            List<List<Point2d>> columnPolys,
            List<double> xs,
            List<double> ys,
            out int movedCount)
        {
            movedCount = 0;

            var ni = new NodeIndex();
            var nodes = ni.Nodes;
            var neighbors = new List<HashSet<int>>();

            var segNodes = new List<int[]>();
            foreach (var seg in segments)
            {
                int ia = ni.GetNode(seg[0]);
                if (ia == neighbors.Count) neighbors.Add(new HashSet<int>());
                int ib = ni.GetNode(seg[1]);
                if (ib == neighbors.Count) neighbors.Add(new HashSet<int>());
                if (ia == ib) continue;
                segNodes.Add(new int[] { ia, ib });
                neighbors[ia].Add(ib);
                neighbors[ib].Add(ia);
            }

            bool OnGridCoord(double v, List<double> coords)
            {
                foreach (var c in coords)
                    if (Math.Abs(v - c) < 1e-6) return true;
                return false;
            }

            var columnCenters = ComputeColumnCenters(columnPolys);

            bool IsFixedNode(Point2d p)
            {
                if (OnGridCoord(p.X, xs) && OnGridCoord(p.Y, ys)) return true;

                foreach (var w in cutSegments)
                    if (IsPointOnSegment(p, w[0], w[1], 1e-3)) return true;

                foreach (var c in columnCenters)
                    if (p.GetDistanceTo(c) < 1e-3) return true;

                int cn = contourPts.Count;
                for (int i = 0; i < cn; i++)
                    if (IsPointOnSegment(p, contourPts[i], contourPts[(i + 1) % cn], 1e-3)) return true;

                return false;
            }

            var isFixed = new bool[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
                isFixed[i] = IsFixedNode(nodes[i]);

            bool SegmentBlocked(Point2d a, Point2d b)
            {
                foreach (var w in cutSegments)
                    if (SegmentsIntersect(a, b, w[0], w[1])) return true;

                int cn = contourPts.Count;
                for (int i = 0; i < cn; i++)
                    if (SegmentsIntersect(a, b, contourPts[i], contourPts[(i + 1) % cn])) return true;

                return false;
            }

            var wasMoved = new bool[nodes.Count];

            for (int iter = 0; iter < 2; iter++)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (isFixed[i] || neighbors[i].Count < 2) continue;

                    double ax = 0, ay = 0;
                    foreach (int nb in neighbors[i]) { ax += nodes[nb].X; ay += nodes[nb].Y; }
                    Point2d newP = new Point2d(ax / neighbors[i].Count, ay / neighbors[i].Count);

                    if (newP.GetDistanceTo(nodes[i]) < 1e-3) continue;
                    if (!IsPointInPolygon(newP, contourPts)) continue;

                    bool inColumn = false;
                    foreach (var col in columnPolys)
                        if (IsPointInPolygon(newP, col)) { inColumn = true; break; }
                    if (inColumn) continue;

                    bool bad = false;
                    foreach (int nb in neighbors[i])
                    {
                        if (newP.GetDistanceTo(nodes[nb]) < MinElementSize - 0.1) { bad = true; break; }
                        if (SegmentBlocked(newP, nodes[nb])) { bad = true; break; }
                    }
                    if (bad) continue;

                    nodes[i] = newP;
                    if (!wasMoved[i]) { wasMoved[i] = true; movedCount++; }
                }
            }

            var result = new List<Point2d[]>();
            foreach (var sn in segNodes)
            {
                Point2d a = nodes[sn[0]], b = nodes[sn[1]];
                if (a.GetDistanceTo(b) < 1e-3) continue;
                result.Add(new Point2d[] { a, b });
            }
            return result;
        }

        // В сетке не допускаются рёбра короче MinElementSize. Узлы, оказавшиеся ближе
        // 100 мм к неподвижной геометрии (стена, контур пилона, центр пилона, контур плиты),
        // притягиваются к ней; пары подвижных узлов ближе 100 мм сливаются в один.
        private List<Point2d[]> WeldShortNodes(
            List<Point2d[]> segments,
            List<Point2d[]> wallSegments,
            List<List<Point2d>> columnPolys,
            List<Point2d> contourPts,
            out int weldedCount)
        {
            weldedCount = 0;
            double weldDist = MinElementSize - 0.1;

            var columnCenters = ComputeColumnCenters(columnPolys);

            var ni = new NodeIndex();
            var nodes = ni.Nodes;

            var segNodes = new List<int[]>();
            foreach (var seg in segments)
                segNodes.Add(new int[] { ni.GetNode(seg[0]), ni.GetNode(seg[1]) });

            bool IsFixedPoint(Point2d p)
            {
                foreach (var w in wallSegments)
                    if (IsPointOnSegment(p, w[0], w[1], 1e-3)) return true;

                foreach (var col in columnPolys)
                {
                    int n = col.Count;
                    for (int i = 0; i < n; i++)
                        if (IsPointOnSegment(p, col[i], col[(i + 1) % n], 1e-3)) return true;
                }

                foreach (var c in columnCenters)
                    if (p.GetDistanceTo(c) < 1e-3) return true;

                int cn = contourPts.Count;
                for (int i = 0; i < cn; i++)
                    if (IsPointOnSegment(p, contourPts[i], contourPts[(i + 1) % cn], 1e-3)) return true;

                return false;
            }

            var isFixed = new bool[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
                isFixed[i] = IsFixedPoint(nodes[i]);

            var target = new int[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
                target[i] = i;

            // Подвижный узел → ближайший фиксированный в радиусе сварки.
            // Кандидаты берутся из пространственной сетки (только соседние бакеты),
            // а не перебором всех узлов плана.
            var fixedGrid = new SpatialGrid(weldDist);
            for (int i = 0; i < nodes.Count; i++)
                if (isFixed[i]) fixedGrid.Add(i, nodes[i]);

            for (int i = 0; i < nodes.Count; i++)
            {
                if (isFixed[i]) continue;
                double bestDist = weldDist;
                int bestJ = -1;
                foreach (int j in fixedGrid.QueryRadius(nodes[i], weldDist))
                {
                    double d = nodes[i].GetDistanceTo(nodes[j]);
                    if (d < bestDist) { bestDist = d; bestJ = j; }
                }
                if (bestJ >= 0) target[i] = bestJ;
            }

            // Оставшиеся пары подвижных узлов ближе 100 мм — слить в узел с меньшим индексом
            var movableGrid = new SpatialGrid(weldDist);
            for (int i = 0; i < nodes.Count; i++)
                if (!isFixed[i]) movableGrid.Add(i, nodes[i]);

            for (int i = 0; i < nodes.Count; i++)
            {
                if (isFixed[i] || target[i] != i) continue;
                foreach (int j in movableGrid.QueryRadius(nodes[i], weldDist))
                {
                    if (j <= i || isFixed[j] || target[j] != j) continue;
                    if (nodes[i].GetDistanceTo(nodes[j]) < weldDist)
                        target[j] = i;
                }
            }

            var result = new List<Point2d[]>();
            for (int k = 0; k < segNodes.Count; k++)
            {
                Point2d a = nodes[target[segNodes[k][0]]];
                Point2d b = nodes[target[segNodes[k][1]]];
                if (a.GetDistanceTo(b) < 1e-3) { weldedCount++; continue; }
                result.Add(new Point2d[] { a, b });
            }
            return result;
        }

        // Координаты линий сетки: равномерный шаг, но линия, оказавшаяся ближе
        // min(30% шага, 100 мм) к грани пилона, смещается на неё — так проще,
        // чем городить наклонные линии.
        private List<double> BuildGridCoords(
            double min, double max, double step,
            List<double> targets,
            out int shiftedCount)
        {
            shiftedCount = 0;

            var coords = new List<double>();
            double v = min;
            while (v < max - 1e-9) { coords.Add(v); v += step; }
            coords.Add(v);

            double maxShift = Math.Min(0.3 * step, 100.0);

            foreach (var t in targets)
            {
                if (t < coords[0] - 1e-9 || t > coords[coords.Count - 1] + 1e-9) continue;

                int best = -1;
                double bestD = maxShift + 1e-9;
                for (int i = 0; i < coords.Count; i++)
                {
                    double d = Math.Abs(coords[i] - t);
                    if (d < bestD) { bestD = d; best = i; }
                }

                if (best >= 0 && Math.Abs(coords[best] - t) > 1e-9)
                {
                    coords[best] = t;
                    shiftedCount++;
                }
            }

            coords.Sort();
            var result = new List<double>();
            foreach (var c in coords)
            {
                if (result.Count > 0 && c - result[result.Count - 1] < 1e-6) continue;
                result.Add(c);
            }
            return result;
        }

        // Открытых узлов не допускается: узел, где линия упёрлась в другую линию и
        // остановилась (в веере направлений инцидентных отрезков есть пустой сектор
        // ≥180°), замыкается наклонной линией в соседний узел. Из кандидатов
        // предпочитается узел, дающий угол ближе к 30/45° к существующим линиям.
        private List<Point2d[]> CloseOpenNodes(
            List<Point2d[]> segments,
            List<Point2d[]> cutSegments,
            List<Point2d> contourPts,
            List<List<Point2d>> columnPolys,
            double cellSize,
            out int closedCount)
        {
            closedCount = 0;

            var ni = new NodeIndex();
            var nodes = ni.Nodes;
            var dirs = new List<List<double>>();

            foreach (var seg in segments)
            {
                int ia = ni.GetNode(seg[0]);
                if (ia == dirs.Count) dirs.Add(new List<double>());
                int ib = ni.GetNode(seg[1]);
                if (ib == dirs.Count) dirs.Add(new List<double>());
                dirs[ia].Add(Math.Atan2(seg[1].Y - seg[0].Y, seg[1].X - seg[0].X));
                dirs[ib].Add(Math.Atan2(seg[0].Y - seg[1].Y, seg[0].X - seg[1].X));
            }

            // Стены и грани пилонов — тоже линии сетки: покрывают направления вдоль себя
            foreach (var w in cutSegments)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (!IsPointOnSegment(nodes[i], w[0], w[1], 1e-3)) continue;
                    if (nodes[i].GetDistanceTo(w[0]) > 1e-3)
                        dirs[i].Add(Math.Atan2(w[0].Y - nodes[i].Y, w[0].X - nodes[i].X));
                    if (nodes[i].GetDistanceTo(w[1]) > 1e-3)
                        dirs[i].Add(Math.Atan2(w[1].Y - nodes[i].Y, w[1].X - nodes[i].X));
                }
            }

            var newSegs = new List<Point2d[]>();
            double twoPi = 2.0 * Math.PI;
            double candidateRadius = 1.6 * cellSize;

            // Кандидаты на замыкание ищутся только среди узлов из соседних бакетов сетки,
            // а не перебором всех узлов плана.
            var candidateGrid = new SpatialGrid(candidateRadius);
            for (int gi = 0; gi < nodes.Count; gi++)
                candidateGrid.Add(gi, nodes[gi]);

            for (int i = 0; i < nodes.Count; i++)
            {
                var dl = dirs[i];
                if (dl.Count < 2) continue;

                // узлы на контуре плиты не замыкаем — наружу нельзя
                bool onContour = false;
                int cn = contourPts.Count;
                for (int k = 0; k < cn; k++)
                    if (IsPointOnSegment(nodes[i], contourPts[k], contourPts[(k + 1) % cn], 1e-3)) { onContour = true; break; }
                if (onContour) continue;

                // узлы на контуре пилона не замыкаем — внутрь пилона сетка не идёт
                bool onColumn = false;
                foreach (var col in columnPolys)
                {
                    int nc = col.Count;
                    for (int k = 0; k < nc; k++)
                        if (IsPointOnSegment(nodes[i], col[k], col[(k + 1) % nc], 1e-3)) { onColumn = true; break; }
                    if (onColumn) break;
                }
                if (onColumn) continue;

                dl.Sort();
                double maxGap = 0, gapStart = 0;
                for (int k = 0; k < dl.Count; k++)
                {
                    double a0 = dl[k];
                    double a1 = (k + 1 < dl.Count) ? dl[k + 1] : dl[0] + twoPi;
                    double gap = a1 - a0;
                    if (gap > maxGap) { maxGap = gap; gapStart = a0; }
                }

                double gapDeg = maxGap * 180.0 / Math.PI;
                bool open = (dl.Count >= 3 && gapDeg >= 179.0) || (dl.Count == 2 && gapDeg >= 200.0);
                if (!open) continue;

                double margin = 15.0 * Math.PI / 180.0;
                double lo = gapStart + margin;
                double hi = gapStart + maxGap - margin;

                int best = -1;
                double bestScore = double.MaxValue;

                foreach (int j in candidateGrid.QueryRadius(nodes[i], candidateRadius))
                {
                    if (j == i) continue;
                    double d = nodes[i].GetDistanceTo(nodes[j]);
                    if (d < MinElementSize - 0.1 || d > candidateRadius) continue;

                    double a = Math.Atan2(nodes[j].Y - nodes[i].Y, nodes[j].X - nodes[i].X);
                    while (a < gapStart) a += twoPi;
                    if (a < lo || a > hi) continue;

                    bool crosses = false;
                    foreach (var w in cutSegments)
                        if (SegmentsIntersect(nodes[i], nodes[j], w[0], w[1])) { crosses = true; break; }
                    if (crosses) continue;

                    foreach (var s in segments)
                        if (SegmentsIntersect(nodes[i], nodes[j], s[0], s[1])) { crosses = true; break; }
                    if (crosses) continue;

                    // Строго внутри плиты: замыкающая линия не может ни пересекать
                    // контур, ни пройти снаружи через выемку вогнутого контура
                    for (int k = 0; k < cn && !crosses; k++)
                        if (SegmentsIntersect(nodes[i], nodes[j], contourPts[k], contourPts[(k + 1) % cn])) crosses = true;
                    if (crosses) continue;
                    Point2d midIJ = new Point2d((nodes[i].X + nodes[j].X) / 2.0, (nodes[i].Y + nodes[j].Y) / 2.0);
                    if (!IsPointInPolygon(midIJ, contourPts)) continue;

                    // отклонение угла новой линии от 30/45° к границам пустого сектора
                    double d0 = (a - gapStart) * 180.0 / Math.PI;
                    double d1 = (gapStart + maxGap - a) * 180.0 / Math.PI;
                    double dev = Math.Min(
                        Math.Min(Math.Abs(d0 - 45.0), Math.Abs(d0 - 30.0)),
                        Math.Min(Math.Abs(d1 - 45.0), Math.Abs(d1 - 30.0)));

                    double score = d / cellSize + dev / 90.0;
                    if (score < bestScore) { bestScore = score; best = j; }
                }

                if (best >= 0)
                {
                    newSegs.Add(new Point2d[] { nodes[i], nodes[best] });
                    closedCount++;
                }
            }

            segments.AddRange(newSegs);
            return segments;
        }

        // Линия сетки не может обрываться посреди другого элемента: каждый узел,
        // лежащий внутри чужого отрезка, делит этот отрезок на два.
        private List<Point2d[]> SplitSegmentsAtNodes(
            List<Point2d[]> segments,
            double cellSize,
            out int splitCount)
        {
            splitCount = 0;

            var nodes = new List<Point2d>();
            var seen = new HashSet<string>();
            foreach (var seg in segments)
            {
                foreach (var p in seg)
                {
                    string key = Math.Round(p.X, 3) + "_" + Math.Round(p.Y, 3);
                    if (seen.Add(key)) nodes.Add(p);
                }
            }

            // Узлы, потенциально лежащие на отрезке, ищутся через пространственную сетку
            // (только узлы в радиусе длины отрезка вокруг его начала), а не перебором всех узлов плана.
            var nodeGrid = new SpatialGrid(Math.Max(cellSize, 1.0));
            for (int ni = 0; ni < nodes.Count; ni++)
                nodeGrid.Add(ni, nodes[ni]);

            var result = new List<Point2d[]>();

            foreach (var seg in segments)
            {
                Point2d a = seg[0], b = seg[1];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double lenSq = dx * dx + dy * dy;
                if (lenSq < 1e-12) continue;
                double segLen = Math.Sqrt(lenSq);

                var cuts = new List<KeyValuePair<double, Point2d>>();
                foreach (int nodeIdx in nodeGrid.QueryRadius(a, segLen))
                {
                    Point2d node = nodes[nodeIdx];
                    if (node.GetDistanceTo(a) < 1e-3 || node.GetDistanceTo(b) < 1e-3) continue;
                    if (!IsPointOnSegment(node, a, b, 1e-3)) continue;

                    double t = ((node.X - a.X) * dx + (node.Y - a.Y) * dy) / lenSq;
                    if (t > 1e-6 && t < 1.0 - 1e-6)
                        cuts.Add(new KeyValuePair<double, Point2d>(t, node));
                }

                if (cuts.Count == 0)
                {
                    result.Add(seg);
                    continue;
                }

                cuts.Sort((p, q) => p.Key.CompareTo(q.Key));
                Point2d prev = a;
                foreach (var cut in cuts)
                {
                    result.Add(new Point2d[] { prev, cut.Value });
                    prev = cut.Value;
                }
                result.Add(new Point2d[] { prev, b });
                splitCount += cuts.Count;
            }

            return result;
        }

        // Каждый угол пилона должен быть связан с сеткой минимум двумя отрезками
        // (полудиагональ к центру + связь наружу). Свободных углов не допускается.
        private List<Point2d[]> EnsureColumnCornerLinks(
            List<Point2d[]> segments,
            List<List<Point2d>> columnPolys,
            double cellSize,
            out int addedCount)
        {
            addedCount = 0;
            if (columnPolys.Count == 0) return segments;

            // Оба прохода (подсчёт инцидентных отрезков и поиск ближайшего узла) ищут
            // кандидатов через пространственную сетку концов отрезков, а не перебором
            // всех отрезков плана на каждый угол пилона.
            double queryRadius = cellSize + 1e-6;
            var endpoints = new List<Point2d>();
            var endpointGrid = new SpatialGrid(cellSize);
            foreach (var seg in segments)
            {
                endpointGrid.Add(endpoints.Count, seg[0]); endpoints.Add(seg[0]);
                endpointGrid.Add(endpoints.Count, seg[1]); endpoints.Add(seg[1]);
            }

            foreach (var col in columnPolys)
            {
                int n = col.Count;
                for (int ci = 0; ci < n; ci++)
                {
                    Point2d corner = col[ci];

                    int incident = 0;
                    foreach (int pi in endpointGrid.QueryRadius(corner, queryRadius))
                    {
                        if (endpoints[pi].GetDistanceTo(corner) < 1e-3)
                            incident++;
                    }
                    if (incident >= 2) continue;

                    // ближайший узел сетки снаружи пилона (не на его контуре)
                    Point2d best = corner;
                    double bestDist = double.MaxValue;
                    foreach (int pi in endpointGrid.QueryRadius(corner, queryRadius))
                    {
                        Point2d p = endpoints[pi];
                        double d = p.GetDistanceTo(corner);
                        if (d < 1e-3 || d > cellSize + 1e-6 || d >= bestDist) continue;
                        if (IsPointInPolygon(p, col)) continue;

                        bool onEdge = false;
                        for (int k = 0; k < n; k++)
                            if (IsPointOnSegment(p, col[k], col[(k + 1) % n], 1e-3)) { onEdge = true; break; }
                        if (onEdge) continue;

                        best = p;
                        bestDist = d;
                    }

                    if (bestDist < double.MaxValue)
                    {
                        segments.Add(new Point2d[] { corner, best });
                        addedCount++;
                    }
                }
            }

            return segments;
        }


        private bool CellInsideAnyColumn(Point2d[] cell, List<List<Point2d>> columns)
        {
            foreach (var col in columns)
            {
                bool allInside = true;
                foreach (var corner in cell)
                {
                    if (!IsPointInPolygon(corner, col)) { allInside = false; break; }
                }
                if (allInside) return true;
            }
            return false;
        }

        private bool PieceInsideAnyColumn(List<Point2d> piece, List<List<Point2d>> columns)
        {
            if (columns.Count == 0) return false;

            double cx = 0, cy = 0;
            foreach (var p in piece) { cx += p.X; cy += p.Y; }
            Point2d centroid = new Point2d(cx / piece.Count, cy / piece.Count);

            foreach (var col in columns)
            {
                if (IsPointInPolygon(centroid, col)) return true;
            }
            return false;
        }


        private void DrawSegment(BlockTableRecord btr, Transaction tr, Point2d a, Point2d b)
        {
            Line line = new Line(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0));
            btr.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
        }

        private void AddQuadSegments(List<Point2d[]> segments, Point2d[] quad)
        {
            segments.Add(new Point2d[] { quad[0], quad[1] });
            segments.Add(new Point2d[] { quad[1], quad[2] });
            segments.Add(new Point2d[] { quad[2], quad[3] });
            segments.Add(new Point2d[] { quad[3], quad[0] });
        }

        private void AddTriSegments(List<Point2d[]> segments, Point2d[] tri)
        {
            segments.Add(new Point2d[] { tri[0], tri[1] });
            segments.Add(new Point2d[] { tri[1], tri[2] });
            segments.Add(new Point2d[] { tri[2], tri[0] });
        }

        // Последний рубеж перед отрисовкой: ни один отрезок сетки не может пересекать
        // контур плиты или лежать снаружи. Пересечение ловится по сторонам контура,
        // выход наружу без пересечения (вогнутый контур, концы на границе) — по середине
        // отрезка; середина, лежащая на самом контуре, наружу не считается.
        private List<Point2d[]> EnforceInsideContour(
            List<Point2d[]> segments,
            List<Point2d> contourPts,
            out int removedOutside)
        {
            removedOutside = 0;
            var result = new List<Point2d[]>();
            int cn = contourPts.Count;

            foreach (var seg in segments)
            {
                bool bad = false;

                for (int i = 0; i < cn && !bad; i++)
                    if (SegmentsIntersect(seg[0], seg[1], contourPts[i], contourPts[(i + 1) % cn]))
                        bad = true;

                if (!bad)
                {
                    Point2d mid = new Point2d((seg[0].X + seg[1].X) / 2.0, (seg[0].Y + seg[1].Y) / 2.0);
                    bool onEdge = false;
                    for (int i = 0; i < cn && !onEdge; i++)
                        if (IsPointOnSegment(mid, contourPts[i], contourPts[(i + 1) % cn], 1e-3))
                            onEdge = true;
                    if (!onEdge && !IsPointInPolygon(mid, contourPts))
                        bad = true;
                }

                if (bad) removedOutside++;
                else result.Add(seg);
            }
            return result;
        }

        // Совпадающие отрезки (общее ребро двух соседних ячеек/треугольников) не должны
        // попадать в DXF дважды: ЛИРА-САПР требует, чтобы отрезки на плане не накладывались.
        private List<Point2d[]> DeduplicateSegments(List<Point2d[]> segments)
        {
            var seen = new HashSet<string>();
            var result = new List<Point2d[]>();
            foreach (var seg in segments)
            {
                string key = EdgeKey(seg[0], seg[1]);
                if (seen.Add(key))
                    result.Add(seg);
            }
            return result;
        }


        private bool SegmentLiesOnContour(Point2d a, Point2d b, List<Point2d> contour, double eps = 1e-3)
        {
            int n = contour.Count;
            for (int i = 0; i < n; i++)
            {
                Point2d c1 = contour[i];
                Point2d c2 = contour[(i + 1) % n];

                if (IsPointOnSegment(a, c1, c2, eps) && IsPointOnSegment(b, c1, c2, eps))
                    return true;
            }
            return false;
        }

        // Внешний контур плиты уже представлен исходной полилинией — отрезки сетки,
        // легшие на него (целиком или как часть более длинного ребра контура), в DXF не нужны.
        private List<Point2d[]> RemoveSegmentsOnContour(
            List<Point2d[]> segments,
            List<Point2d> contour,
            out int removedCount)
        {
            var result = new List<Point2d[]>();
            removedCount = 0;
            foreach (var seg in segments)
            {
                if (SegmentLiesOnContour(seg[0], seg[1], contour))
                    removedCount++;
                else
                    result.Add(seg);
            }
            return result;
        }


        private class CollinearGroup
        {
            public List<double[]> Mesh = new List<double[]>();
            public List<double[]> Blocked = new List<double[]>();
            public List<KeyValuePair<double, Point2d>> Breaks =
                new List<KeyValuePair<double, Point2d>>();
        }

        private void AddSegmentToLineGroups(Dictionary<string, CollinearGroup> groups, Point2d a, Point2d b, bool blocked)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return;

            double nx = dx / len, ny = dy / len;
            if (nx < -1e-9 || (Math.Abs(nx) <= 1e-9 && ny < 0)) { nx = -nx; ny = -ny; }
            double c = a.X * ny - a.Y * nx;

            string key = nx.ToString("F6") + "|" + ny.ToString("F6") + "|" + c.ToString("F1");

            CollinearGroup g;
            if (!groups.TryGetValue(key, out g))
            {
                g = new CollinearGroup();
                groups[key] = g;
            }

            double t0 = a.X * nx + a.Y * ny;
            double t1 = b.X * nx + b.Y * ny;
            Point2d pa = a, pb = b;
            if (t0 > t1)
            {
                double tt = t0; t0 = t1; t1 = tt;
                Point2d tp = pa; pa = pb; pb = tp;
            }

            g.Breaks.Add(new KeyValuePair<double, Point2d>(t0, pa));
            g.Breaks.Add(new KeyValuePair<double, Point2d>(t1, pb));
            (blocked ? g.Blocked : g.Mesh).Add(new double[] { t0, t1 });
        }

        // Финальная зачистка: никакие два отрезка (и отрезок со стеной) не должны
        // накладываться даже частично. Коллинеарные отрезки на одной прямой разбиваются
        // концами друг друга на элементарные интервалы; каждый интервал выводится один раз,
        // интервалы, накрытые стеной, выбрасываются (стена уже нарисована пользователем).
        private List<Point2d[]> ResolveOverlappingSegments(
            List<Point2d[]> meshSegments,
            List<Point2d[]> wallSegments,
            out int removedOnWalls,
            out int mergedOverlaps)
        {
            removedOnWalls = 0;
            mergedOverlaps = 0;

            var groups = new Dictionary<string, CollinearGroup>();
            foreach (var seg in meshSegments)
                AddSegmentToLineGroups(groups, seg[0], seg[1], false);
            foreach (var seg in wallSegments)
                AddSegmentToLineGroups(groups, seg[0], seg[1], true);

            var result = new List<Point2d[]>();

            foreach (var g in groups.Values)
            {
                if (g.Mesh.Count == 0) continue;

                g.Breaks.Sort((p, q) => p.Key.CompareTo(q.Key));

                var ts = new List<double>();
                var pts = new List<Point2d>();
                foreach (var br in g.Breaks)
                {
                    if (ts.Count > 0 && br.Key - ts[ts.Count - 1] < 1e-3) continue;
                    ts.Add(br.Key);
                    pts.Add(br.Value);
                }

                for (int i = 0; i + 1 < ts.Count; i++)
                {
                    double mid = (ts[i] + ts[i + 1]) / 2.0;

                    int cover = 0;
                    foreach (var iv in g.Mesh)
                        if (mid >= iv[0] - 1e-6 && mid <= iv[1] + 1e-6) cover++;
                    if (cover == 0) continue;

                    bool blocked = false;
                    foreach (var iv in g.Blocked)
                        if (mid >= iv[0] - 1e-6 && mid <= iv[1] + 1e-6) { blocked = true; break; }

                    if (blocked) { removedOnWalls++; continue; }
                    if (cover > 1) mergedOverlaps += cover - 1;

                    result.Add(new Point2d[] { pts[i], pts[i + 1] });
                }
            }

            return result;
        }


        // Ячейка "задета" стеной, если стена проходит через неё хотя бы частично.
        private bool CellTouchesWalls(Point2d[] cell, List<Point2d[]> wallSegments)
        {
            if (wallSegments.Count == 0) return false;

            var cellPoly = new List<Point2d>(cell);
            foreach (var w in wallSegments)
            {
                if (IsPointInPolygon(w[0], cellPoly) || IsPointInPolygon(w[1], cellPoly)) return true;

                for (int k = 0; k < 4; k++)
                {
                    if (SegmentsIntersect(w[0], w[1], cell[k], cell[(k + 1) % 4])) return true;
                }
            }
            return false;
        }

        private bool SegmentTouchesPolygon(Point2d a, Point2d b, List<Point2d> poly)
        {
            if (IsPointInPolygon(a, poly) || IsPointInPolygon(b, poly)) return true;

            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                if (SegmentsIntersect(a, b, poly[i], poly[(i + 1) % n])) return true;
            }

            Point2d mid = new Point2d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
            return IsPointInPolygon(mid, poly);
        }

        // Последовательно разрезает полигон ячейки по линии каждой задевшей его стены:
        // обе полуплоскости через Sutherland-Hodgman. Точки разреза общие для соседних
        // частей — стена входит в сетку как рёбра с совпадающими узлами.
        private List<List<Point2d>> SplitPolygonByWalls(
            List<Point2d> poly,
            List<Point2d[]> wallSegments)
        {
            var pieces = new List<List<Point2d>> { poly };

            foreach (var w in wallSegments)
            {
                var next = new List<List<Point2d>>();

                foreach (var piece in pieces)
                {
                    if (!SegmentTouchesPolygon(w[0], w[1], piece))
                    {
                        next.Add(piece);
                        continue;
                    }

                    var left = CleanupPolygon(ClipPolygonAgainstEdge(piece, w[0], w[1]));
                    var right = CleanupPolygon(ClipPolygonAgainstEdge(piece, w[1], w[0]));

                    bool added = false;
                    if (left.Count >= 3 && Math.Abs(PolygonArea(left)) > 1e-3) { next.Add(left); added = true; }
                    if (right.Count >= 3 && Math.Abs(PolygonArea(right)) > 1e-3) { next.Add(right); added = true; }
                    if (!added) next.Add(piece);
                }

                pieces = next;
            }

            return pieces;
        }


    }
}
