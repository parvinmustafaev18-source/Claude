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
        // Мозаика качества элементов (как оценка качества КЭ в ЛИРА-САПР):
        // каждая пластина будущей задачи заливается цветом своей градации α.
        // Имена слоёв мозаики и слоя «ПЛОХИЕ» — в Defs.cs.

        // Границы градаций: α >= QualityAlphaMid — хороший элемент,
        // α < QualityAlphaBad — плохой; между ними — приемлемый.
        private const double QualityAlphaMid = 0.5;
        private const double QualityAlphaBad = 0.3;

        // Перепроверка качества ДЕЙСТВУЮЩЕЙ сетки после ручных правок: старые
        // заливки/контуры стираются, элементы собираются заново из линий чертежа
        // (LINE_TRIANGULATION + WALLS + контур). Основная оценка идёт при
        // построении в MESHQUADMESH; эта команда — для повторных прогонов без
        // перестройки сетки. Ручные правки могут оставить линии без общих узлов —
        // Х-пересечения режутся в BuildQualityPlates, но линии, не дотянутые друг
        // до друга дальше допусков, дадут кривые ячейки.
        [CommandMethod("MESHQUALITY")]
        public void MeshQualityCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHQUALITY");
            Database db = doc.Database;

            PromptEntityOptions peo = new PromptEntityOptions("\nВыберите контур плиты (полилинию): ");
            peo.SetRejectMessage("\nНужна полилиния.");
            peo.AddAllowedClass(typeof(Polyline), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;

            PromptKeywordOptions pkoM = new PromptKeywordOptions("\nПоказ качества элементов");
            pkoM.Keywords.Add("Critical", "Critical", "Только критические эл.");
            pkoM.Keywords.Add("Mosaic", "Mosaic", "Полная мозаика");
            pkoM.Keywords.Default = "Critical";
            PromptResult prM = ed.GetKeywords(pkoM);
            if (prM.Status != PromptStatus.OK && prM.Status != PromptStatus.None) return;
            bool badOnly = !(prM.Status == PromptStatus.OK && prM.StringResult == "Mosaic");

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

                // Все предыдущие результаты оценки стираются
                EraseMarksOnLayer(tr, db, QualityGoodLayerName);
                EraseMarksOnLayer(tr, db, QualityMidLayerName);
                EraseMarksOnLayer(tr, db, QualityBadLayerName);
                EraseMarksOnLayer(tr, db, BadElementsLayerName);

                // При отказе валидации транзакция коммитится: до этого места команда
                // ничего не меняла (стёртые заливки — результат прошлой оценки).
                if (!ValidateContour(pline, ed, tr, db, out var contourPts)) { tr.Commit(); return; }
                EnsureCcw(contourPts);

                var segments = new List<Point2d[]>();
                int cn = contourPts.Count;
                for (int i = 0; i < cn; i++)
                    segments.Add(new Point2d[] { contourPts[i], contourPts[(i + 1) % cn] });

                var columnCenters = new List<Point2d>();

                // Отверстия (проёмы) читаются так же, как в экспорте: их стороны идут в
                // планарный граф (иначе кромка проёма для графа не существует и грань
                // вокруг него разливается внутрь), а пластины внутри проёма отбрасываются
                // по центру — оценивать качество несуществующих элементов незачем.
                var holePolys = new List<List<Point2d>>();

                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || string.IsNullOrEmpty(ent.Layer)) continue;

                    if (ent is DBPoint dbp && IsColumnLayer(ent.Layer))
                    {
                        columnCenters.Add(new Point2d(dbp.Position.X, dbp.Position.Y));
                        continue;
                    }

                    if (ent.Layer == HoleLayerName)
                    {
                        if (ent is Polyline hpl && hpl.Closed)
                        {
                            var hv = GetPolylineVertices(hpl);
                            if (hv.Count >= 3)
                            {
                                EnsureCcw(hv);
                                holePolys.Add(hv);
                                int hc = hv.Count;
                                for (int i = 0; i < hc; i++)
                                    segments.Add(new Point2d[] { hv[i], hv[(i + 1) % hc] });
                            }
                        }
                        continue;
                    }

                    bool meshLayer = ent.Layer == TriangulationLayerName || IsWallLayer(ent.Layer);
                    if (!meshLayer) continue;

                    if (ent is Line line)
                    {
                        segments.Add(new Point2d[]
                        {
                            new Point2d(line.StartPoint.X, line.StartPoint.Y),
                            new Point2d(line.EndPoint.X, line.EndPoint.Y)
                        });
                    }
                    else if (ent is Polyline wp)
                    {
                        var verts = GetPolylineVertices(wp);
                        int segCount = wp.Closed ? verts.Count : verts.Count - 1;
                        for (int i = 0; i < segCount; i++)
                            segments.Add(new Point2d[] { verts[i], verts[(i + 1) % verts.Count] });
                    }
                }

                var plates = BuildQualityPlates(segments, columnCenters, out int failedFaces);

                // Пластины внутри проёма — это дырка, а не элементы: центр пластины
                // (среднее вершин) всегда лежит строго внутри неё, поэтому тест не
                // срывается на проёме любой формы. Та же логика, что в экспорте.
                int platesInHoles = 0;
                if (holePolys.Count > 0)
                {
                    var kept = new List<Point2d[]>();
                    foreach (var pl in plates)
                    {
                        double cx = 0, cy = 0;
                        foreach (var p in pl) { cx += p.X; cy += p.Y; }
                        Point2d pc = new Point2d(cx / pl.Length, cy / pl.Length);
                        bool inHole = false;
                        foreach (var hp in holePolys)
                            if (IsPointInPolygon(pc, hp)) { inHole = true; break; }
                        if (inHole) { platesInHoles++; continue; }
                        kept.Add(pl);
                    }
                    plates = kept;
                    ed.WriteMessage($"\nОтверстий (проёмов): {holePolys.Count}, пластин внутри них не оценивалось: {platesInHoles}\n");
                }

                if (plates.Count == 0)
                {
                    ed.WriteMessage("\nНе найдено ни одной замкнутой ячейки сетки — сначала постройте сетку (MESHQUADMESH).\n");
                    tr.Commit(); // стёртые старые заливки сохраняются
                    return;
                }

                // Тот же инвариант, что в экспорте: пластины обязаны покрыть плиту за
                // вычетом проёмов. Здесь он проверяет ДЕЙСТВУЮЩУЮ сетку в чертеже, ещё
                // до экспорта, — расхождение означает дыру или наложение элементов.
                double holesArea;
                double targetArea = SlabTargetArea(contourPts, holePolys, out holesArea);
                ReportAreaBalance(ed, PlatesArea(plates), targetArea, holesArea, plates.Count);

                DrawQualityMarks(tr, db, ed, plates, badOnly, failedFaces);

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHQUALITY: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // Сегменты сетки -> пластины (3-4 вершины) той же логикой, что экспорт
        // в ЛИРУ: шип конца стены -> веер вокруг конца, грань с центром пилона ->
        // веер вокруг центра, 3/4 узла -> элемент, больше -> триангуляция.
        private List<Point2d[]> BuildQualityPlates(
            List<Point2d[]> segments,
            List<Point2d> columnCenters,
            out int failedFaces)
        {
            failedFaces = 0;

            segments = DeduplicateSegments(segments);
            segments = SplitSegmentsAtIntersections(segments, out _);
            segments = SplitSegmentsAtNodes(segments, 500.0, out _);
            segments = DeduplicateSegments(segments);

            var ni = new NodeIndex();
            var nodes = ni.Nodes;
            var edges = new List<int[]>();
            foreach (var seg in segments)
            {
                int ia = ni.GetNode(seg[0]);
                int ib = ni.GetNode(seg[1]);
                if (ia != ib) edges.Add(new int[] { ia, ib });
            }

            var faces = ExtractPlanarFaces(nodes, edges);

            var plates = new List<Point2d[]>();
            foreach (var rawFace in faces)
            {
                var face = new List<int>(rawFace);
                var spikeTips = new List<int>();
                bool spikeRemoved = true;
                while (spikeRemoved && face.Count >= 3)
                {
                    spikeRemoved = false;
                    int fm = face.Count;
                    for (int i = 0; i < fm; i++)
                    {
                        if (face[(i - 1 + fm) % fm] != face[(i + 1) % fm]) continue;
                        spikeTips.Add(face[i]);
                        int iNext = (i + 1) % fm;
                        if (iNext > i) { face.RemoveAt(iNext); face.RemoveAt(i); }
                        else { face.RemoveAt(i); face.RemoveAt(iNext); }
                        spikeRemoved = true;
                        break;
                    }
                }
                if (face.Count < 3) continue;

                var poly = new List<Point2d>();
                foreach (int idx in face) poly.Add(nodes[idx]);

                if (spikeTips.Count > 0)
                {
                    Point2d s = nodes[spikeTips[0]];
                    for (int i = 0; i < face.Count; i++)
                    {
                        Point2d va = nodes[face[i]];
                        Point2d vb = nodes[face[(i + 1) % face.Count]];
                        if (Math.Abs(CrossProduct(va, vb, s)) < 1.0) continue;
                        plates.Add(new Point2d[] { s, va, vb });
                    }
                    continue;
                }

                int colIdx = -1;
                for (int c = 0; c < columnCenters.Count; c++)
                    if (IsPointInPolygon(columnCenters[c], poly)) { colIdx = c; break; }

                if (colIdx >= 0)
                {
                    Point2d cc = columnCenters[colIdx];
                    for (int i = 0; i < poly.Count; i++)
                    {
                        Point2d va = poly[i];
                        Point2d vb = poly[(i + 1) % poly.Count];
                        if (Math.Abs(CrossProduct(va, vb, cc)) < 1.0) continue;
                        plates.Add(new Point2d[] { cc, va, vb });
                    }
                }
                else if (poly.Count == 3 || poly.Count == 4)
                {
                    plates.Add(poly.ToArray());
                }
                else
                {
                    foreach (var t in TriangulateSimplePolygon(poly, ref failedFaces))
                        plates.Add(t);
                }
            }

            return plates;
        }

        // Отрисовка оценки качества: badOnly — только контуры критических
        // элементов белыми полилиниями в слое ПЛОХИЕ; иначе полная мозаика
        // заливок Solid по градациям α. Слои должны быть очищены заранее.
        private void DrawQualityMarks(Transaction tr, Database db, Editor ed, List<Point2d[]> plates, bool badOnly, int failedFaces)
        {
            BlockTableRecord btrW = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            int goodCount = 0, midCount = 0, badCount = 0, triCount = 0, quadCount = 0;
            double worstAlpha = double.MaxValue;
            Point2d worstCenter = Point2d.Origin;
            var solidIds = new ObjectIdCollection();

            Point3d P3(Point2d p) { return new Point3d(p.X, p.Y, 0.0); }
            void AddSolid(string layer, Point2d a, Point2d b, Point2d c, Point2d? d)
            {
                // Порядок вершин четырёхугольника у Solid — "змейкой" (p0 p1 p3 p2),
                // иначе получается бабочка
                Solid sol = d.HasValue
                    ? new Solid(P3(a), P3(b), P3(d.Value), P3(c))
                    : new Solid(P3(a), P3(b), P3(c));
                sol.Layer = layer;
                btrW.AppendEntity(sol);
                tr.AddNewlyCreatedDBObject(sol, true);
                solidIds.Add(sol.ObjectId);
            }

            foreach (var plate in plates)
            {
                // Вырожденный элемент не рисуется: нулевая площадь у Solid
                // может отрисоваться произвольным пятном
                if (Math.Abs(PolygonArea(new List<Point2d>(plate))) < 1e-3) continue;

                double alpha = plate.Length == 3
                    ? TriangleAlpha(plate[0], plate[1], plate[2])
                    : QuadAlpha(plate);
                if (plate.Length == 3) triCount++; else quadCount++;

                string layerName;
                short colorIndex;
                if (alpha >= QualityAlphaMid) { layerName = QualityGoodLayerName; colorIndex = 3; goodCount++; }
                else if (alpha >= QualityAlphaBad) { layerName = QualityMidLayerName; colorIndex = 2; midCount++; }
                else { layerName = QualityBadLayerName; colorIndex = 1; badCount++; }

                if (alpha < worstAlpha)
                {
                    worstAlpha = alpha;
                    worstCenter = PolygonCentroid(new List<Point2d>(plate));
                }

                if (badOnly)
                {
                    if (alpha >= QualityAlphaBad) continue;
                    EnsureLayer(db, tr, BadElementsLayerName, 7); // белый
                    Polyline pl = new Polyline();
                    for (int i = 0; i < plate.Length; i++)
                        pl.AddVertexAt(i, plate[i], 0.0, 0.0, 0.0);
                    pl.Closed = true;
                    pl.Layer = BadElementsLayerName;
                    pl.LineWeight = LineWeight.LineWeight035;
                    btrW.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    continue;
                }

                EnsureLayer(db, tr, layerName, colorIndex);
                if (plate.Length == 3)
                {
                    AddSolid(layerName, plate[0], plate[1], plate[2], null);
                }
                else if (IsConvexQuad(plate))
                {
                    AddSolid(layerName, plate[0], plate[1], plate[2], plate[3]);
                }
                else
                {
                    // Вогнутый четырёхугольник Solid заливает с перехлёстом —
                    // делится на два треугольника по внутренней диагонали
                    double a012 = PolygonArea(new List<Point2d> { plate[0], plate[1], plate[2] });
                    double a023 = PolygonArea(new List<Point2d> { plate[0], plate[2], plate[3] });
                    if (a012 * a023 > 0)
                    {
                        AddSolid(layerName, plate[0], plate[1], plate[2], null);
                        AddSolid(layerName, plate[0], plate[2], plate[3], null);
                    }
                    else
                    {
                        AddSolid(layerName, plate[1], plate[2], plate[3], null);
                        AddSolid(layerName, plate[1], plate[3], plate[0], null);
                    }
                }
            }

            // Заливки — под линии сетки, чтобы мозаика не прятала чертёж
            if (solidIds.Count > 0)
            {
                DrawOrderTable dot = (DrawOrderTable)tr.GetObject(btrW.DrawOrderTableId, OpenMode.ForWrite);
                dot.MoveToBottom(solidIds);
            }

            ed.WriteMessage($"\nОценка качества (α по методике ЛИРА-САПР): элементов {triCount + quadCount} (треугольников {triCount}, четырёхугольников {quadCount})\n");
            ed.WriteMessage($"  зелёных (α>={QualityAlphaMid:0.0#}): {goodCount}, жёлтых ({QualityAlphaBad:0.0#}-{QualityAlphaMid:0.0#}): {midCount}, красных (α<{QualityAlphaBad:0.0#}): {badCount}\n");
            if (triCount + quadCount > 0)
                ed.WriteMessage($"  худший α={worstAlpha:0.00} в ({worstCenter.X:0}, {worstCenter.Y:0})\n");
            if (badOnly)
                ed.WriteMessage(badCount > 0
                    ? $"  контуры красных элементов — белые полилинии в слое {BadElementsLayerName}\n"
                    : "  критических элементов нет — слой " + BadElementsLayerName + " пуст\n");
            if (failedFaces > 0)
                ed.WriteMessage($"\nВНИМАНИЕ: не удалось разбить ячеек при оценке: {failedFaces}\n");
        }

        // Точка пересечения внутренностей двух отрезков (не касание концом — те
        // случаи закрывает SplitSegmentsAtNodes). Коллинеарные наложения не в счёт:
        // после разрезания по узлам их куски совпадают и уходят в DeduplicateSegments.
        private bool SegmentCrossingPoint(Point2d p1, Point2d p2, Point2d p3, Point2d p4, out Point2d ip)
        {
            ip = Point2d.Origin;
            double d1x = p2.X - p1.X, d1y = p2.Y - p1.Y;
            double d2x = p4.X - p3.X, d2y = p4.Y - p3.Y;
            double denom = d1x * d2y - d1y * d2x;
            if (Math.Abs(denom) < 1e-12) return false;

            double t = ((p3.X - p1.X) * d2y - (p3.Y - p1.Y) * d2x) / denom;
            double u = ((p3.X - p1.X) * d1y - (p3.Y - p1.Y) * d1x) / denom;
            if (t < 0.0 || t > 1.0 || u < 0.0 || u > 1.0) return false;

            ip = new Point2d(p1.X + d1x * t, p1.Y + d1y * t);
            // ближе 0.5 мм к любому концу — узловое касание, не Х-пересечение
            if (ip.GetDistanceTo(p1) < MeshTol.Crossing || ip.GetDistanceTo(p2) < 0.5
                || ip.GetDistanceTo(p3) < MeshTol.Crossing || ip.GetDistanceTo(p4) < MeshTol.Crossing) return false;
            return true;
        }

        // Разрезание всех отрезков в точках их взаимных Х-пересечений: планарный
        // граф строится по общим узлам, и пересечение без узла делает грань,
        // накрывающую чужие линии. Поиск пар — через пространственную сетку по
        // серединам отрезков, чтобы не перебирать все пары на больших планах.
        private List<Point2d[]> SplitSegmentsAtIntersections(List<Point2d[]> segments, out int crossings)
        {
            crossings = 0;
            int n = segments.Count;
            double maxLen = 1.0;
            var mids = new Point2d[n];
            var halfLen = new double[n];
            for (int i = 0; i < n; i++)
            {
                double len = segments[i][0].GetDistanceTo(segments[i][1]);
                if (len > maxLen) maxLen = len;
                mids[i] = new Point2d((segments[i][0].X + segments[i][1].X) / 2.0,
                                      (segments[i][0].Y + segments[i][1].Y) / 2.0);
                halfLen[i] = len / 2.0;
            }

            var grid = new SpatialGrid(maxLen);
            for (int i = 0; i < n; i++) grid.Add(i, mids[i]);

            var cuts = new List<double>[n];
            for (int i = 0; i < n; i++)
            {
                foreach (int j in grid.QueryRadius(mids[i], halfLen[i] + maxLen / 2.0 + 1.0))
                {
                    if (j <= i) continue;
                    if (!SegmentCrossingPoint(segments[i][0], segments[i][1], segments[j][0], segments[j][1], out Point2d ip))
                        continue;

                    crossings++;
                    double li = segments[i][0].GetDistanceTo(segments[i][1]);
                    double lj = segments[j][0].GetDistanceTo(segments[j][1]);
                    if (cuts[i] == null) cuts[i] = new List<double>();
                    if (cuts[j] == null) cuts[j] = new List<double>();
                    cuts[i].Add(segments[i][0].GetDistanceTo(ip) / li);
                    cuts[j].Add(segments[j][0].GetDistanceTo(ip) / lj);
                }
            }

            var result = new List<Point2d[]>();
            for (int i = 0; i < n; i++)
            {
                if (cuts[i] == null) { result.Add(segments[i]); continue; }
                cuts[i].Sort();
                Point2d a = segments[i][0], b = segments[i][1];
                Point2d prev = a;
                foreach (double t in cuts[i])
                {
                    Point2d p = new Point2d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                    if (prev.GetDistanceTo(p) > MeshTol.Crossing) { result.Add(new Point2d[] { prev, p }); prev = p; }
                }
                if (prev.GetDistanceTo(b) > MeshTol.Crossing) result.Add(new Point2d[] { prev, b });
            }
            return result;
        }
    }
}
