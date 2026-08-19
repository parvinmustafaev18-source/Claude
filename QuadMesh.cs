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
            EchoCommandStart(ed, "MESHQUADMESH");
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

            // Отверстия (проёмы): опциональный дополнительный выбор замкнутых контуров,
            // внутри которых сетка не строится. Enter без выбора — отверстий нет; ранее
            // созданные отверстия всё равно подхватятся со слоя MESH_HOLES.
            var holeIds = new List<ObjectId>();
            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите замкнутые контуры отверстий/проёмов (Enter — без отверстий): ";
            pso.AllowDuplicates = false;
            SelectionFilter holeFilter = new SelectionFilter(
                new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
            PromptSelectionResult psr = ed.GetSelection(pso, holeFilter);
            if (psr.Status == PromptStatus.OK)
                holeIds.AddRange(psr.Value.GetObjectIds());

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

                // Старые маркеры проблем и мозаика качества стираются при каждом
                // запуске: после перестройки сетки они относятся к прошлой сетке
                EraseMarksOnLayer(tr, db, ProblemLayerName);
                EraseMarksOnLayer(tr, db, BadElementsLayerName);

                // Дуги и полилинии вне плоскости XY читаются как ломаные в WCS —
                // предупреждаем до построения, иначе расхождение всплывёт только в ЛИРЕ.
                WarnBadPolylines(tr, db, ed);

                // При отказе валидации транзакция коммитится: до этого места команда
                // ничего не меняла, а маркеры разрывов/углов без коммита откатились бы.
                if (!ValidateContour(pline, ed, tr, db, out var contourPts)) { tr.Commit(); return; }
                EnsureCcw(contourPts);

                var bb = PolygonBBox(contourPts);
                double minX = bb[0], minY = bb[1];

                // ---- ЧТЕНИЕ ЧЕРТЕЖА -------------------------------------------------
                // Снап двигает объекты чертежа к линиям сетки, поэтому он живёт здесь,
                // а не в расчётном ядре: ядро чертежа не видит вовсе.

                int snappedWalls = SnapWallsToGrid(tr, db, minX, minY, cellSize);
                var wallSegments = GetWallSegments(tr, db);
                var doorEnds = GetDoorEndpoints(tr, db); // косяки дверных проёмов — узлы сетки
                ed.WriteMessage($"\nНайдено сегментов стен: {wallSegments.Count}, подвинуто к узлам сетки (до {WallSnapTolerance:0} мм): {snappedWalls}\n");

                // Пилоны (слой COLUMNS): контур пилона врезается в сетку как стены,
                // внутренность пилона остаётся пустой — только точка в центре.
                int snappedColumns = SnapColumnsToGrid(tr, db, minX, minY, cellSize);
                var columnPolys = GetColumnPolygons(tr, db);
                ed.WriteMessage($"\nНайдено пилонов: {columnPolys.Count}, подвинуто к сетке (до {WallSnapTolerance:0} мм): {snappedColumns}\n");

                // Отверстия (проёмы) в плите: замкнутые контуры, внутри которых сетки
                // нет. Геометрически это та же «пустота», что и внутренность пилона, но
                // без центральной точки, без крест-оси и без экспорта пластинами.
                // Интерактивно выбранные контуры переносятся на служебный слой MESH_HOLES
                // и там же накапливаются между запусками; MESHEXPORTTXT по этому слою
                // исключает грань отверстия из заливки элементами.
                int movedHoles = MovePolylinesToHoleLayer(tr, db, holeIds, per.ObjectId);
                var holePolys = GetHolePolygons(tr, db);
                if (holePolys.Count > 0)
                    ed.WriteMessage($"\nОтверстий (проёмов) в плите: {holePolys.Count}" + (movedHoles > 0 ? $" (перенесено на слой {HoleLayerName}: {movedHoles})" : "") + "\n");

                // Отпечаток контура пилона-пластины (слой MESH_PYLONS). В отличие от
                // COLUMNS это НЕ пустота: сетка плиты внутри есть, только мелкая. От
                // контура требуется одно — стать линиями сетки, чтобы углы пилона были
                // узлами, а не висели посреди элемента.
                var pylonRects = GetPylonOutlines(tr, db, out int rectsFromAxes, out int rectsNotRect);
                if (pylonRects.Count > 0)
                    ed.WriteMessage($"\nКонтуров пилонов для отпечатка: {pylonRects.Count}" +
                        (rectsFromAxes > 0 ? $" (восстановлено по осям, без контура на {PylonOutlineLayerName}: {rectsFromAxes})" : "") +
                        (rectsNotRect > 0 ? $", пропущено повёрнутых/непрямоугольных: {rectsNotRect}" : "") + "\n");

                // Косяки дверных проёмов: этот проход собирает только их координаты —
                // они идут «мягкими» целями в BuildGridCoords. Сами поперечные
                // ограничения строятся позже, ПОСЛЕ снапа дверей к готовой сетке
                // (обратный вызов SnapDoors ниже), иначе разрезы остались бы на
                // старых местах.
                var jambXs = new List<double>();
                var jambYs = new List<double>();
                GetDoorJambConstraints(tr, db, cellSize, jambXs, jambYs);

                // Оси пилонов-пластин: концы и центр — тоже мягкие цели выравнивания.
                var axisXs = new List<double>();
                var axisYs = new List<double>();
                GetPylonAxisTargets(tr, db, axisXs, axisYs);

                var input = new MeshInput
                {
                    Contour = contourPts,
                    CellSize = cellSize,
                    WallSegments = wallSegments,
                    DoorEnds = doorEnds,
                    ColumnPolys = columnPolys,
                    HolePolys = holePolys,
                    PylonRects = pylonRects,
                    PylonCrosses = GetPylonCrossConstraints(tr, db),
                    JambXs = jambXs,
                    JambYs = jambYs,
                    AxisXs = axisXs,
                    AxisYs = axisYs,

                    // Двери подтягиваются к линиям сетки, а это правка чертежа: ядро
                    // вызывает этот код сразу после построения координат сетки.
                    // Квадраты-обозначения перерисовываются по новым серединам.
                    SnapDoors = (xs, ys, log) =>
                    {
                        int snappedDoors = SnapDoorsToGrid(tr, db, xs, ys);
                        if (snappedDoors > 0)
                        {
                            log.Add($"\nДверных отрезков подтянуто к узлам сетки (до {DoorSnapTolerance:0} мм): {snappedDoors}\n");
                            RedrawAllDoorMarks(tr, db);
                        }
                        return GetDoorJambConstraints(tr, db, cellSize, new List<double>(), new List<double>());
                    }
                };

                // ---- РАСЧЁТ ---------------------------------------------------------
                var mesh = BuildMeshCore(input);
                foreach (var line in mesh.Log) ed.WriteMessage(line);

                if (!mesh.Ok)
                {
                    // Нарушено жёсткое правило. Откат основной транзакции (снап стен,
                    // пилонов, перенос отверстий), маркеры — своей.
                    tr.Abort();
                    MarkProblemPoints(db, mesh.ErrorPts);
                    return;
                }

                // ---- ОТРИСОВКА ------------------------------------------------------
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                foreach (var seg in mesh.Segments)
                    DrawSegment(btr, tr, seg[0], seg[1]);

                // Контур пилона разбивается на отрезки и переносится в слой линий
                // триангуляции (полилиния удаляется, точка центра остаётся в COLUMNS).
                int explodedColumns = ExplodeColumnContours(tr, db);
                if (explodedColumns > 0)
                    ed.WriteMessage($"\nКонтуров пилонов разбито на отрезки в {TriangulationLayerName}: {explodedColumns}\n");

                // Проблемные места сетки — красные круги в слое проблем (список и
                // сообщение о нём собрало ядро).
                if (mesh.ProblemPts.Count > 0)
                    DrawMarkCircles(tr, db, ProblemLayerName, mesh.ProblemPts, ProblemMarkRadius);

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
            List<List<Point2d>> fixedRegions,
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

                // Узлы отпечатка пилона (углы, грани, мелкая сетка внутри) неподвижны:
                // сглаживание увело бы их с граней, и отпечаток перестал бы совпадать
                // с контуром пилона.
                if (PointInOrOnAnyPolygon(p, fixedRegions)) return true;

                foreach (var w in cutSegments)
                    if (IsPointOnSegment(p, w[0], w[1], MeshTol.OnSegment)) return true;

                foreach (var c in columnCenters)
                    if (p.GetDistanceTo(c) < MeshTol.NodeMerge) return true;

                int cn = contourPts.Count;
                for (int i = 0; i < cn; i++)
                    if (IsPointOnSegment(p, contourPts[i], contourPts[(i + 1) % cn], MeshTol.OnSegment)) return true;

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

                    if (newP.GetDistanceTo(nodes[i]) < MeshTol.NodeMerge) continue;
                    if (!IsPointInPolygon(newP, contourPts)) continue;

                    bool inColumn = false;
                    foreach (var col in columnPolys)
                        if (IsPointInPolygon(newP, col)) { inColumn = true; break; }
                    if (inColumn) continue;

                    // Внутрь отпечатка пилона чужой узел заходить не должен — там своя
                    // мелкая сетка.
                    if (PointInOrOnAnyPolygon(newP, fixedRegions)) continue;

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
                if (a.GetDistanceTo(b) < MeshTol.NodeMerge) continue;
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
            List<List<Point2d>> fixedRegions,
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
                // Отпечаток пилона неподвижен целиком: иначе узел его грани, оказавшийся
                // ближе 100 мм к оси пилона (у пилона тоньше 200 мм так всегда),
                // притянулся бы к оси и отпечаток схлопнулся бы на неё.
                if (PointInOrOnAnyPolygon(p, fixedRegions)) return true;

                foreach (var w in wallSegments)
                    if (IsPointOnSegment(p, w[0], w[1], MeshTol.OnSegment)) return true;

                foreach (var col in columnPolys)
                {
                    int n = col.Count;
                    for (int i = 0; i < n; i++)
                        if (IsPointOnSegment(p, col[i], col[(i + 1) % n], MeshTol.OnSegment)) return true;
                }

                foreach (var c in columnCenters)
                    if (p.GetDistanceTo(c) < MeshTol.NodeMerge) return true;

                int cn = contourPts.Count;
                for (int i = 0; i < cn; i++)
                    if (IsPointOnSegment(p, contourPts[i], contourPts[(i + 1) % cn], MeshTol.OnSegment)) return true;

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
                if (a.GetDistanceTo(b) < MeshTol.NodeMerge) { weldedCount++; continue; }
                result.Add(new Point2d[] { a, b });
            }
            return result;
        }

        // Координаты линий сетки: равномерный шаг, но линия, оказавшаяся ближе
        // min(30% шага, 100 мм) к грани пилона, смещается на неё — так проще,
        // чем городить наклонные линии.
        // Режет отрезки в заданных точках (концах дверных проёмов): если точка лежит
        // строго внутри отрезка, он делится на два с общим узлом. Так в сетке
        // появляются узлы на косяках двери, и куски стены точно совпадают с проёмом.
        private List<Point2d[]> SplitSegmentsAtPoints(List<Point2d[]> segments, List<Point2d> points, double tol, out int splitCount)
        {
            splitCount = 0;
            if (points == null || points.Count == 0) return segments;

            var result = new List<Point2d[]>();
            foreach (var seg in segments)
            {
                var cuts = new List<Point2d>();
                foreach (var p in points)
                {
                    if (p.GetDistanceTo(seg[0]) < tol || p.GetDistanceTo(seg[1]) < tol) continue; // уже узел
                    if (IsPointOnSegment(p, seg[0], seg[1], tol)) cuts.Add(p);
                }
                if (cuts.Count == 0) { result.Add(seg); continue; }

                cuts.Sort((a, b) => seg[0].GetDistanceTo(a).CompareTo(seg[0].GetDistanceTo(b)));
                Point2d cur = seg[0];
                foreach (var c in cuts)
                {
                    if (cur.GetDistanceTo(c) > tol) { result.Add(new Point2d[] { cur, c }); cur = c; splitCount++; }
                }
                if (cur.GetDistanceTo(seg[1]) > tol) result.Add(new Point2d[] { cur, seg[1] });
            }
            return result;
        }

        // Координаты линий сетки: равномерный шаг + подгонка под «цели» — грани
        // пилонов, кромки отверстий, косяки дверей, концы и середины осей пилонов.
        //
        // targets     — «мягкие» цели: линия двигается на цель, только если она в
        //               пределах maxShift; иначе цель просто не достигается.
        // hardTargets — «жёсткие» цели (кромки отверстий): линия на цели
        //               гарантируется — ближайшую двигаем, если рядом, иначе
        //               вставляем новую.
        //
        // Два правила, без которых подгонка сама портит сетку:
        //  1. ОДНА ЛИНИЯ — ОДНА ЦЕЛЬ. Раньше цели обрабатывались подряд и могли
        //     двигать одну и ту же линию: последняя побеждала, а первая оставалась
        //     неудовлетворённой, хотя рядом была свободная линия. Теперь занятая
        //     линия помечается и другой цели не отдаётся.
        //  2. НИКАКИХ СЛАЙВЕРОВ. Сдвиг или вставка, после которых просвет до соседней
        //     линии меньше MinGridGap, не выполняется: именно так у осей пилонов и
        //     косяков дверей появлялись элементы в единицы миллиметров. Мягкая цель в
        //     этом случае отбрасывается, жёсткая — сообщается в rejectedCount.
        //
        // Цели обрабатываются от ближайших к своей линии к дальним: близкой цели
        // сдвиг почти ничего не стоит, и она не должна проигрывать дальней.
        private List<double> BuildGridCoords(
            double min, double max, double step,
            List<double> targets,
            List<double> hardTargets,
            out int shiftedCount,
            out int insertedCount,
            out int rejectedCount)
        {
            shiftedCount = 0;
            insertedCount = 0;
            rejectedCount = 0;

            var coords = new List<double>();
            double v = min;
            while (v < max - MeshTol.Zero) { coords.Add(v); v += step; }
            coords.Add(v);

            double maxShift = MeshTol.MaxShift(step);
            double minGap = MeshTol.MinGridGap(step);

            // Линии, которые двигать и удалять нельзя: закреплённые за целью и две
            // крайние. Крайние — это границы плана: сдвинув или убрав их, мы оставим
            // у края плиты полосу, не покрытую ни одной ячейкой, то есть дыру в сетке.
            var locked = new HashSet<double>();
            locked.Add(coords[0]);
            locked.Add(coords[coords.Count - 1]);

            // Просвет до соседей, если линию с индексом idx поставить в позицию t
            // (idx < 0 — линия вставляется новой).
            bool GapOk(int idx, double t)
            {
                for (int i = 0; i < coords.Count; i++)
                {
                    if (i == idx) continue;
                    if (Math.Abs(coords[i] - t) < minGap - MeshTol.Zero) return false;
                }
                return true;
            }

            // Ближайшая к цели НЕзакреплённая линия.
            int NearestFree(double t, out double dist)
            {
                int best = -1;
                dist = double.MaxValue;
                for (int i = 0; i < coords.Count; i++)
                {
                    if (locked.Contains(coords[i])) continue;
                    double d = Math.Abs(coords[i] - t);
                    if (d < dist) { dist = d; best = i; }
                }
                return best;
            }

            // Цели: убираем дубликаты и вышедшие за границы плана, сортируем по
            // расстоянию до ближайшей линии.
            List<double> PrepareTargets(List<double> src)
            {
                var list = new List<double>();
                if (src == null) return list;
                foreach (var t in src)
                {
                    if (t < coords[0] - MeshTol.Zero || t > coords[coords.Count - 1] + MeshTol.Zero) continue;
                    bool dup = false;
                    foreach (var u in list)
                        if (Math.Abs(u - t) < MeshTol.NodeMerge) { dup = true; break; }
                    if (!dup) list.Add(t);
                }
                list.Sort((a, b) =>
                {
                    double da, db;
                    NearestFree(a, out da);
                    NearestFree(b, out db);
                    return da.CompareTo(db);
                });
                return list;
            }

            // Жёсткие цели идут первыми: линию на кромке отверстия обязаны получить
            // все, а мягкая цель — только если осталась свободная линия.
            foreach (var t in PrepareTargets(hardTargets))
            {
                double d;
                int best = NearestFree(t, out d);

                // Линия уже стоит на цели (могла быть поставлена другой целью).
                bool already = false;
                foreach (var c in coords)
                    if (Math.Abs(c - t) < MeshTol.NodeMerge) { already = true; break; }
                if (already) { locked.Add(t); continue; }

                if (best >= 0 && d <= maxShift && GapOk(best, t))
                {
                    coords[best] = t;
                    locked.Add(t);
                    shiftedCount++;
                }
                else
                {
                    // Линия на кромке отверстия обязана быть, поэтому вставляем свою.
                    // Обычные линии сетки, оказавшиеся к ней ближе минимального
                    // просвета, убираем: ячейка станет шире, зато полосы в единицы
                    // миллиметров не будет. Закреплённые за другими целями линии не
                    // трогаем — если помешала такая, цель пропускается.
                    coords.RemoveAll(c => !locked.Contains(c) && Math.Abs(c - t) < minGap - MeshTol.Zero);

                    if (GapOk(-1, t))
                    {
                        coords.Add(t);
                        locked.Add(t);
                        insertedCount++;
                    }
                    else
                    {
                        rejectedCount++;
                    }
                }
                coords.Sort();
            }

            foreach (var t in PrepareTargets(targets))
            {
                bool already = false;
                foreach (var c in coords)
                    if (Math.Abs(c - t) < MeshTol.NodeMerge) { already = true; break; }
                if (already) { locked.Add(t); continue; }

                double d;
                int best = NearestFree(t, out d);
                // Цель дальше допустимого сдвига — обычное дело (ради мягкой цели
                // линию через весь план не вставляем), в счётчик конфликтов не идёт.
                if (best >= 0 && d > maxShift) continue;
                if (best < 0) { rejectedCount++; continue; }      // все линии рядом заняты
                if (!GapOk(best, t)) { rejectedCount++; continue; } // сдвиг дал бы слайвер

                coords[best] = t;
                locked.Add(t);
                shiftedCount++;
                coords.Sort();
            }

            coords.Sort();
            var result = new List<double>();
            foreach (var c in coords)
            {
                if (result.Count > 0 && c - result[result.Count - 1] < MeshTol.NodeMerge) continue;
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
            out int closedCount,
            List<Point2d> unclosedNodes)
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
                    if (!IsPointOnSegment(nodes[i], w[0], w[1], MeshTol.OnSegment)) continue;
                    if (nodes[i].GetDistanceTo(w[0]) > MeshTol.NodeMerge)
                        dirs[i].Add(Math.Atan2(w[0].Y - nodes[i].Y, w[0].X - nodes[i].X));
                    if (nodes[i].GetDistanceTo(w[1]) > MeshTol.NodeMerge)
                        dirs[i].Add(Math.Atan2(w[1].Y - nodes[i].Y, w[1].X - nodes[i].X));
                }
            }

            var newSegs = new List<Point2d[]>();
            double twoPi = 2.0 * Math.PI;
            double candidateRadius = MeshTol.CloseRadiusFactor * cellSize;

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
                    if (IsPointOnSegment(nodes[i], contourPts[k], contourPts[(k + 1) % cn], MeshTol.OnSegment)) { onContour = true; break; }
                if (onContour) continue;

                // узлы на контуре пилона не замыкаем — внутрь пилона сетка не идёт
                bool onColumn = false;
                foreach (var col in columnPolys)
                {
                    int nc = col.Count;
                    for (int k = 0; k < nc; k++)
                        if (IsPointOnSegment(nodes[i], col[k], col[(k + 1) % nc], MeshTol.OnSegment)) { onColumn = true; break; }
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

                Point2d bestTarget = Point2d.Origin;
                bool hasBest = false;
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

                    // И с замыканиями, добавленными в этом же проходе. Проверки против
                    // одного лишь segments недостаточно: две наклонные, каждая из
                    // которых не пересекала исходную сетку, спокойно пересекались друг
                    // с другом — крест без узла посреди элемента.
                    foreach (var s in newSegs)
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
                    if (score < bestScore) { bestScore = score; bestTarget = nodes[j]; hasBest = true; }
                }

                // Кандидаты на самом контуре плиты: проекция открытого узла на ближайшие
                // стороны. Узел, упёршийся у границы без подходящего соседа в сетке,
                // дотягивается до контура; новый узел на контуре чуть дороже готового.
                for (int k = 0; k < cn; k++)
                {
                    Point2d c1 = contourPts[k];
                    Point2d c2 = contourPts[(k + 1) % cn];
                    double ex = c2.X - c1.X, ey = c2.Y - c1.Y;
                    double elenSq = ex * ex + ey * ey;
                    if (elenSq < MeshTol.ZeroSq) continue;

                    double t = ((nodes[i].X - c1.X) * ex + (nodes[i].Y - c1.Y) * ey) / elenSq;
                    if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
                    Point2d proj = new Point2d(c1.X + ex * t, c1.Y + ey * t);

                    double d = nodes[i].GetDistanceTo(proj);
                    if (d < MinElementSize - 0.1 || d > candidateRadius) continue;

                    double a = Math.Atan2(proj.Y - nodes[i].Y, proj.X - nodes[i].X);
                    while (a < gapStart) a += twoPi;
                    if (a < lo || a > hi) continue;

                    bool crosses = false;
                    foreach (var w in cutSegments)
                        if (SegmentsIntersect(nodes[i], proj, w[0], w[1])) { crosses = true; break; }
                    if (crosses) continue;

                    foreach (var s in segments)
                        if (SegmentsIntersect(nodes[i], proj, s[0], s[1])) { crosses = true; break; }
                    if (crosses) continue;

                    for (int m = 0; m < cn && !crosses; m++)
                        if (SegmentsIntersect(nodes[i], proj, contourPts[m], contourPts[(m + 1) % cn])) crosses = true;
                    if (crosses) continue;
                    Point2d midIP = new Point2d((nodes[i].X + proj.X) / 2.0, (nodes[i].Y + proj.Y) / 2.0);
                    if (!IsPointInPolygon(midIP, contourPts)) continue;

                    double d0 = (a - gapStart) * 180.0 / Math.PI;
                    double d1 = (gapStart + maxGap - a) * 180.0 / Math.PI;
                    double dev = Math.Min(
                        Math.Min(Math.Abs(d0 - 45.0), Math.Abs(d0 - 30.0)),
                        Math.Min(Math.Abs(d1 - 45.0), Math.Abs(d1 - 30.0)));

                    double score = d / cellSize + dev / 90.0 + 0.1;
                    if (score < bestScore) { bestScore = score; bestTarget = proj; hasBest = true; }
                }

                // Кандидаты на гранях пустот (пилоны И отверстия): проекция открытого узла
                // на ближайшую сторону пустоты. Узел у кромки проёма, которому сосед через
                // пустоту недопустим (пересёк бы cutSegments), дотягивается до самой кромки —
                // на грани отверстия создаётся узел. Это выполняет требование: при обрезке
                // сетки узлы обязаны садиться на грань отверстия, как на контур плиты.
                foreach (var vpoly in columnPolys)
                {
                    int vn = vpoly.Count;
                    for (int k = 0; k < vn; k++)
                    {
                        Point2d c1 = vpoly[k];
                        Point2d c2 = vpoly[(k + 1) % vn];
                        double ex = c2.X - c1.X, ey = c2.Y - c1.Y;
                        double elenSq = ex * ex + ey * ey;
                        if (elenSq < MeshTol.ZeroSq) continue;

                        double t = ((nodes[i].X - c1.X) * ex + (nodes[i].Y - c1.Y) * ey) / elenSq;
                        if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
                        Point2d proj = new Point2d(c1.X + ex * t, c1.Y + ey * t);

                        double d = nodes[i].GetDistanceTo(proj);
                        if (d < MinElementSize - 0.1 || d > candidateRadius) continue;

                        double a = Math.Atan2(proj.Y - nodes[i].Y, proj.X - nodes[i].X);
                        while (a < gapStart) a += twoPi;
                        if (a < lo || a > hi) continue;

                        bool crosses = false;
                        foreach (var w in cutSegments)
                            if (SegmentsIntersect(nodes[i], proj, w[0], w[1])) { crosses = true; break; }
                        if (crosses) continue;

                        foreach (var s in segments)
                            if (SegmentsIntersect(nodes[i], proj, s[0], s[1])) { crosses = true; break; }
                        if (crosses) continue;

                        for (int m = 0; m < cn && !crosses; m++)
                            if (SegmentsIntersect(nodes[i], proj, contourPts[m], contourPts[(m + 1) % cn])) crosses = true;
                        if (crosses) continue;

                        // Замыкающая линия должна остаться в плите и не нырять внутрь пустоты.
                        Point2d midIP = new Point2d((nodes[i].X + proj.X) / 2.0, (nodes[i].Y + proj.Y) / 2.0);
                        if (!IsPointInPolygon(midIP, contourPts)) continue;
                        bool midInVoid = false;
                        foreach (var vp2 in columnPolys)
                            if (IsPointInPolygon(midIP, vp2)) { midInVoid = true; break; }
                        if (midInVoid) continue;

                        double d0 = (a - gapStart) * 180.0 / Math.PI;
                        double d1 = (gapStart + maxGap - a) * 180.0 / Math.PI;
                        double dev = Math.Min(
                            Math.Min(Math.Abs(d0 - 45.0), Math.Abs(d0 - 30.0)),
                            Math.Min(Math.Abs(d1 - 45.0), Math.Abs(d1 - 30.0)));

                        double score = d / cellSize + dev / 90.0 + 0.1;
                        if (score < bestScore) { bestScore = score; bestTarget = proj; hasBest = true; }
                    }
                }

                if (hasBest)
                {
                    newSegs.Add(new Point2d[] { nodes[i], bestTarget });
                    closedCount++;
                }
                else
                {
                    // Открытый узел, для которого не нашлось допустимого замыкания, —
                    // из-за расположения объектов сетка здесь остаётся с обрывом.
                    unclosedNodes?.Add(nodes[i]);
                }
            }

            segments.AddRange(newSegs);
            return segments;
        }

        // Линия сетки не может обрываться посреди другого элемента: каждый узел,
        // лежащий внутри чужого отрезка, делит этот отрезок на два.
        // ВАЖНО про совпадающие рёбра. Если до сюда дожили два наложенных коллинеарных
        // отрезка (A-B и A-C, где C лежит внутри A-B), разрез A-B по узлу C даёт кусок
        // A-C, который в списке уже есть, — на выходе получается пара совпадающих
        // рёбер, а в ЛИРЕ наложенные элементы. Функция сама породила этот кусок, ей
        // его и не выпускать дважды: каждое ребро уходит в результат один раз, счётчик
        // отброшенных возвращается наружу. Наложение при этом схлопывается правильно:
        // A-B и A-C превращаются в A-C и C-B.
        private List<Point2d[]> SplitSegmentsAtNodes(
            List<Point2d[]> segments,
            double cellSize,
            out int splitCount,
            out int droppedDuplicates)
        {
            splitCount = 0;
            droppedDuplicates = 0;

            // Список уникальных узлов: совпадение по допуску слияния, а не по
            // округлённым координатам (иначе один узел мог попасть в список дважды
            // и «резать» отрезок сам об себя).
            var ni = new NodeIndex();
            foreach (var seg in segments)
            {
                ni.GetNode(seg[0]);
                ni.GetNode(seg[1]);
            }
            var nodes = ni.Nodes;

            // Узлы, потенциально лежащие на отрезке, ищутся через пространственную сетку
            // (только узлы в радиусе длины отрезка вокруг его начала), а не перебором всех узлов плана.
            var nodeGrid = new SpatialGrid(Math.Max(cellSize, 1.0));
            for (int i = 0; i < nodes.Count; i++)
                nodeGrid.Add(i, nodes[i]);

            var result = new List<Point2d[]>();
            var emitted = new HashSet<long>();

            foreach (var seg in segments)
            {
                Point2d a = seg[0], b = seg[1];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double lenSq = dx * dx + dy * dy;
                if (lenSq < MeshTol.ZeroSq) continue;
                double segLen = Math.Sqrt(lenSq);

                var cuts = new List<KeyValuePair<double, Point2d>>();
                foreach (int nodeIdx in nodeGrid.QueryRadius(a, segLen))
                {
                    Point2d node = nodes[nodeIdx];
                    if (node.GetDistanceTo(a) < MeshTol.NodeMerge || node.GetDistanceTo(b) < MeshTol.NodeMerge) continue;
                    if (!IsPointOnSegment(node, a, b, MeshTol.OnSegment)) continue;

                    double t = ((node.X - a.X) * dx + (node.Y - a.Y) * dy) / lenSq;
                    if (t > 1e-6 && t < 1.0 - 1e-6)
                        cuts.Add(new KeyValuePair<double, Point2d>(t, node));
                }

                if (cuts.Count == 0)
                {
                    if (!Emit(result, emitted, ni, a, b)) droppedDuplicates++;
                    continue;
                }

                cuts.Sort((p, q) => p.Key.CompareTo(q.Key));
                Point2d prev = a;
                foreach (var cut in cuts)
                {
                    if (!Emit(result, emitted, ni, prev, cut.Value)) droppedDuplicates++;
                    prev = cut.Value;
                }
                if (!Emit(result, emitted, ni, prev, b)) droppedDuplicates++;
                splitCount += cuts.Count;
            }

            return result;
        }

        // Кладёт ребро в результат, если такого там ещё нет. Возвращает false, когда
        // ребро отброшено как совпадающее.
        private bool Emit(List<Point2d[]> result, HashSet<long> emitted, NodeIndex ni, Point2d a, Point2d b)
        {
            if (!emitted.Add(EdgePairKey(ni.GetNode(a), ni.GetNode(b)))) return false;
            result.Add(new Point2d[] { a, b });
            return true;
        }

        // Узлы контура пилона: углы плюс точки мелкой сетки на его гранях. Ровно этими
        // точками отпечаток обязан войти в сетку плиты.
        private List<Point2d> CollectPylonOutlineNodes(List<List<Point2d>> rects)
        {
            var pts = new List<Point2d>();
            foreach (var r in rects)
            {
                double[] b = PolyBbox(r);
                var fx = BuildPylonInnerCoords(b[0], b[2]);
                var fy = BuildPylonInnerCoords(b[1], b[3]);

                foreach (var x in fx)
                {
                    pts.Add(new Point2d(x, b[1]));
                    pts.Add(new Point2d(x, b[3]));
                }
                foreach (var y in fy)
                {
                    pts.Add(new Point2d(b[0], y));
                    pts.Add(new Point2d(b[2], y));
                }
            }
            return pts;
        }

        // ЖЁСТКОЕ ПРАВИЛО: каждый узел контура пилона обязан быть узлом сетки плиты.
        // Ребро, проходящее через такой узел насквозь, режется в нём надвое — узел
        // перестаёт быть «висячим» посреди чужого элемента и связывается с плитой.
        // Это тот же приём, что для косяков дверей, но с поиском кандидатов через
        // пространственную сетку: узлов контура на плане тысячи, и перебор всех пар
        // «отрезок × точка» стоил бы десятки миллионов проверок.
        private List<Point2d[]> SplitSegmentsAtPylonNodes(
            List<Point2d[]> segments,
            List<Point2d> pts,
            double cellSize,
            out int splitCount)
        {
            splitCount = 0;
            if (pts == null || pts.Count == 0) return segments;

            var grid = new SpatialGrid(Math.Max(cellSize, 1.0));
            for (int i = 0; i < pts.Count; i++)
                grid.Add(i, pts[i]);

            var result = new List<Point2d[]>();

            foreach (var seg in segments)
            {
                Point2d a = seg[0], b = seg[1];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double lenSq = dx * dx + dy * dy;
                if (lenSq < MeshTol.ZeroSq) continue;
                double segLen = Math.Sqrt(lenSq);

                var cuts = new List<KeyValuePair<double, Point2d>>();
                foreach (int pi in grid.QueryRadius(a, segLen))
                {
                    Point2d p = pts[pi];
                    if (p.GetDistanceTo(a) < MeshTol.NodeMerge || p.GetDistanceTo(b) < MeshTol.NodeMerge) continue;
                    if (!IsPointOnSegment(p, a, b, MeshTol.OnSegment)) continue;

                    double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
                    if (t > 1e-6 && t < 1.0 - 1e-6)
                        cuts.Add(new KeyValuePair<double, Point2d>(t, p));
                }

                if (cuts.Count == 0) { result.Add(seg); continue; }

                cuts.Sort((p, q) => p.Key.CompareTo(q.Key));
                Point2d prev = a;
                foreach (var cut in cuts)
                {
                    if (prev.GetDistanceTo(cut.Value) < MeshTol.NodeMerge) continue;
                    result.Add(new Point2d[] { prev, cut.Value });
                    prev = cut.Value;
                    splitCount++;
                }
                if (prev.GetDistanceTo(b) > MeshTol.NodeMerge)
                    result.Add(new Point2d[] { prev, b });
            }

            return result;
        }

        // Постусловие того же правила, проверяется после ВСЕХ обрезок: из узла контура
        // пилона обязано выходить не меньше двух рёбер сетки. Ноль — узел в сетку не
        // вошёл вовсе, один — вошёл тупиковым концом. Функция ничего не чинит: молчаливое
        // исправление скрыло бы сбой этапа, а место нарушения важнее самого нарушения.
        private List<Point2d> FindUnlinkedPylonNodes(
            List<Point2d[]> segments,
            List<Point2d> pts,
            double cellSize)
        {
            var bad = new List<Point2d>();
            if (pts == null || pts.Count == 0) return bad;

            var endpoints = new List<Point2d>();
            var grid = new SpatialGrid(Math.Max(cellSize, 1.0));
            foreach (var seg in segments)
            {
                grid.Add(endpoints.Count, seg[0]); endpoints.Add(seg[0]);
                grid.Add(endpoints.Count, seg[1]); endpoints.Add(seg[1]);
            }

            foreach (var p in pts)
            {
                int incident = 0;
                foreach (int ei in grid.QueryRadius(p, MeshTol.MinElementSize))
                {
                    if (endpoints[ei].GetDistanceTo(p) < MeshTol.NodeMerge) incident++;
                    if (incident >= 2) break;
                }
                if (incident < 2) bad.Add(p);
            }
            return bad;
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

                    // Заодно с подсчётом запоминаем, С КЕМ угол уже связан: концы
                    // отрезков лежат в endpoints парами (0-1, 2-3, ...), поэтому
                    // второй конец инцидентного отрезка — сосед по индексу (pi ^ 1).
                    int incident = 0;
                    var linked = new List<Point2d>(2);
                    foreach (int pi in endpointGrid.QueryRadius(corner, queryRadius))
                    {
                        if (endpoints[pi].GetDistanceTo(corner) >= MeshTol.NodeMerge) continue;
                        incident++;
                        linked.Add(endpoints[pi ^ 1]);
                    }
                    if (incident >= 2) continue;

                    // ближайший узел сетки снаружи пилона (не на его контуре)
                    Point2d best = corner;
                    double bestDist = double.MaxValue;
                    foreach (int pi in endpointGrid.QueryRadius(corner, queryRadius))
                    {
                        Point2d p = endpoints[pi];
                        double d = p.GetDistanceTo(corner);
                        if (d < MeshTol.NodeMerge || d > cellSize + 1e-6 || d >= bestDist) continue;
                        if (IsPointInPolygon(p, col)) continue;

                        // Единственный имеющийся отрезок угла может вести ровно в тот
                        // узел, который здесь ищется как ближайший, — тогда связь
                        // добавилась бы вторым слоем поверх существующей. В ЛИРЕ это
                        // наложенные элементы; на выдуманных планах самотеста такие
                        // рёбра появлялись в 24 случаях из 30.
                        bool already = false;
                        foreach (var q in linked)
                            if (q.GetDistanceTo(p) < MeshTol.NodeMerge) { already = true; break; }
                        if (already) continue;

                        bool onEdge = false;
                        for (int k = 0; k < n; k++)
                            if (IsPointOnSegment(p, col[k], col[(k + 1) % n], MeshTol.OnSegment)) { onEdge = true; break; }
                        if (onEdge) continue;

                        best = p;
                        bestDist = d;
                    }

                    if (bestDist < double.MaxValue)
                    {
                        segments.Add(new Point2d[] { corner, best });
                        addedCount++;

                        // Новая связь тоже идёт в индекс: у соседнего пилона может
                        // оказаться тот же угол, и без этого он добавил бы её ещё раз.
                        endpointGrid.Add(endpoints.Count, corner); endpoints.Add(corner);
                        endpointGrid.Add(endpoints.Count, best); endpoints.Add(best);
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

        // Ячейка считается внутренней по ЦЕНТРУ (в отличие от CellInsideAnyColumn,
        // требующего все 4 угла внутри). Нужно для проёмов: ячейка, чья внешняя грань
        // лежит точно на кромке, имеет углы на границе полигона, и проверка по углам её
        // пропускала. Центр прямоугольной ячейки всегда строго внутри своего проёма.
        private bool CellCenterInsideAnyColumn(Point2d[] cell, List<List<Point2d>> columns)
        {
            if (columns.Count == 0) return false;

            double cx = 0, cy = 0;
            foreach (var p in cell) { cx += p.X; cy += p.Y; }
            Point2d c = new Point2d(cx / cell.Length, cy / cell.Length);

            foreach (var col in columns)
            {
                if (IsPointInPolygon(c, col)) return true;
            }
            return false;
        }

        // Ячейка целиком накрыта прямоугольником отпечатка. Сравниваются габариты, а не
        // принадлежность точек полигону: и ячейка, и отпечаток осевыровнены, а грань
        // отпечатка после снапа обычно ЛЕЖИТ на линии сетки — «строго внутри» в этом
        // случае даёт false для всех четырёх углов.
        private bool CellInsideAnyRect(Point2d[] cell, List<List<Point2d>> rects)
        {
            if (rects.Count == 0) return false;
            double tol = MeshTol.OnSegment;

            double cminX = double.MaxValue, cminY = double.MaxValue;
            double cmaxX = double.MinValue, cmaxY = double.MinValue;
            foreach (var p in cell)
            {
                if (p.X < cminX) cminX = p.X;
                if (p.X > cmaxX) cmaxX = p.X;
                if (p.Y < cminY) cminY = p.Y;
                if (p.Y > cmaxY) cmaxY = p.Y;
            }

            foreach (var r in rects)
            {
                double rminX = double.MaxValue, rminY = double.MaxValue;
                double rmaxX = double.MinValue, rmaxY = double.MinValue;
                foreach (var p in r)
                {
                    if (p.X < rminX) rminX = p.X;
                    if (p.X > rmaxX) rmaxX = p.X;
                    if (p.Y < rminY) rminY = p.Y;
                    if (p.Y > rmaxY) rmaxY = p.Y;
                }

                if (cminX >= rminX - tol && cmaxX <= rmaxX + tol
                    && cminY >= rminY - tol && cmaxY <= rmaxY + tol) return true;
            }
            return false;
        }

        private static double[] PolyBbox(IList<Point2d> pts)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue;
            foreach (var p in pts)
            {
                if (p.X < x0) x0 = p.X;
                if (p.X > x1) x1 = p.X;
                if (p.Y < y0) y0 = p.Y;
                if (p.Y > y1) y1 = p.Y;
            }
            return new double[] { x0, y0, x1, y1 };
        }

        // Ячейка и отпечаток перекрываются по площади (касание стороной не в счёт).
        private bool CellOverlapsAnyRect(Point2d[] cell, List<List<Point2d>> rects)
        {
            double tol = MeshTol.OnSegment;
            double[] c = PolyBbox(cell);
            foreach (var r in rects)
            {
                double[] b = PolyBbox(r);
                if (Math.Min(c[2], b[2]) - Math.Max(c[0], b[0]) > tol
                    && Math.Min(c[3], b[3]) - Math.Max(c[1], b[1]) > tol) return true;
            }
            return false;
        }

        // Ячейка минус отпечатки пилонов. И ячейка, и отпечатки осевыровнены, поэтому
        // разность — снова прямоугольники (до четырёх на каждое вычитание): полоса под
        // отпечатком, над ним, слева и справа от него. Никаких косых рёбер и вееров,
        // в отличие от разреза полуплоскостями по граням.
        //
        // Слишком узкая полоса (грань пилона прошла в паре миллиметров от линии сетки)
        // намеренно НЕ выбрасывается — дыра в плите хуже. Её схлопнет WeldShortNodes:
        // подвижный узел полосы ближе 100 мм к неподвижной грани отпечатка притянется
        // к ней, и полоса исчезнет вместе с вырожденными рёбрами.
        private List<Point2d[]> SubtractRects(Point2d[] cell, List<List<Point2d>> rects)
        {
            double tol = MeshTol.OnSegment;
            var work = new List<double[]> { PolyBbox(cell) };

            foreach (var r in rects)
            {
                double[] b = PolyBbox(r);
                var next = new List<double[]>();

                foreach (var a in work)
                {
                    double ox0 = Math.Max(a[0], b[0]), ox1 = Math.Min(a[2], b[2]);
                    double oy0 = Math.Max(a[1], b[1]), oy1 = Math.Min(a[3], b[3]);

                    if (ox1 - ox0 <= tol || oy1 - oy0 <= tol) { next.Add(a); continue; }

                    if (oy0 - a[1] > tol) next.Add(new double[] { a[0], a[1], a[2], oy0 });
                    if (a[3] - oy1 > tol) next.Add(new double[] { a[0], oy1, a[2], a[3] });
                    if (ox0 - a[0] > tol) next.Add(new double[] { a[0], oy0, ox0, oy1 });
                    if (a[2] - ox1 > tol) next.Add(new double[] { ox1, oy0, a[2], oy1 });
                }

                work = next;
            }

            var result = new List<Point2d[]>();
            foreach (var a in work)
            {
                result.Add(new Point2d[]
                {
                    new Point2d(a[0], a[1]), new Point2d(a[2], a[1]),
                    new Point2d(a[2], a[3]), new Point2d(a[0], a[3])
                });
            }
            return result;
        }

        // Координаты мелкой сетки внутри отпечатка по одной оси. Каждая половина (от
        // грани до оси пилона) делится на равные части, поэтому и ГРАНИ, и ОСЬ всегда
        // остаются линиями сетки: ось обязана быть ребром — по ней экспорт режет
        // пластину, а центральный узел пилона терять нельзя. Число частей — floor, а не
        // round: round(150/100)=2 дал бы элементы по 75 мм, вдвое меньше минимального.
        private List<double> BuildPylonInnerCoords(double a, double b)
        {
            var result = new List<double>();
            double c = (a + b) / 2.0;
            double half = (b - a) / 2.0;

            int n = (int)Math.Floor(half / MeshTol.PylonInnerCell);
            if (n < 1) n = 1;
            double step = half / n;

            for (int i = 0; i < n; i++) result.Add(a + step * i);
            result.Add(c);
            for (int i = 1; i < n; i++) result.Add(c + step * i);
            result.Add(b);
            return result;
        }

        // Точка внутри полигона ИЛИ на его стороне. Для отпечатка пилона важна именно
        // такая проверка: узлы его граней обязаны считаться «своими» наравне с узлами
        // мелкой сетки внутри, иначе постобработка двигает и сваривает грань.
        private bool PointInOrOnAnyPolygon(Point2d p, List<List<Point2d>> polys)
        {
            if (polys == null) return false;
            foreach (var poly in polys)
            {
                if (IsPointInPolygon(p, poly)) return true;
                int n = poly.Count;
                for (int i = 0; i < n; i++)
                    if (IsPointOnSegment(p, poly[i], poly[(i + 1) % n], MeshTol.OnSegment)) return true;
            }
            return false;
        }

        // Точка строго внутри хотя бы одной пустоты (проёма/пилона).
        private bool PointInsideAnyVoid(Point2d p, List<List<Point2d>> voids)
        {
            if (voids == null) return false;
            foreach (var v in voids)
                if (IsPointInPolygon(p, v)) return true;
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
        // контур плиты или лежать снаружи, но и удалять пересекающий отрезок целиком
        // нельзя — сетка перестаёт дотягиваться до границы. Отрезок режется точками
        // пересечения со сторонами контура на части; наружные части (по середине,
        // с учётом вогнутого контура) отбрасываются, внутренние остаются, их концы
        // ложатся точно на контур. Части короче 1 мм считаются мусором.
        private List<Point2d[]> ClipSegmentsToContour(
            List<Point2d[]> segments,
            List<Point2d> contourPts,
            out int clippedCount,
            out int removedOutside)
        {
            clippedCount = 0;
            removedOutside = 0;
            var result = new List<Point2d[]>();
            int cn = contourPts.Count;

            foreach (var seg in segments)
            {
                Point2d a = seg[0], b = seg[1];
                double len = a.GetDistanceTo(b);
                if (len < 1e-9) continue;

                // Параметры (0..1) точек пересечения отрезка со сторонами контура
                var ts = new List<double> { 0.0, 1.0 };
                for (int i = 0; i < cn; i++)
                {
                    Point2d c1 = contourPts[i];
                    Point2d c2 = contourPts[(i + 1) % cn];
                    if (!SegmentsIntersect(a, b, c1, c2)) continue;

                    double denom = (b.X - a.X) * (c2.Y - c1.Y) - (b.Y - a.Y) * (c2.X - c1.X);
                    if (Math.Abs(denom) < 1e-12) continue;
                    double t = ((c1.X - a.X) * (c2.Y - c1.Y) - (c1.Y - a.Y) * (c2.X - c1.X)) / denom;
                    if (t > 1e-9 && t < 1.0 - 1e-9) ts.Add(t);
                }
                ts.Sort();

                int keptParts = 0;
                bool trimmed = false;
                for (int i = 0; i + 1 < ts.Count; i++)
                {
                    double t0 = ts[i], t1 = ts[i + 1];
                    if ((t1 - t0) * len < MeshTol.MinPiece) { trimmed = true; continue; }

                    Point2d p0 = new Point2d(a.X + (b.X - a.X) * t0, a.Y + (b.Y - a.Y) * t0);
                    Point2d p1 = new Point2d(a.X + (b.X - a.X) * t1, a.Y + (b.Y - a.Y) * t1);
                    Point2d mid = new Point2d((p0.X + p1.X) / 2.0, (p0.Y + p1.Y) / 2.0);

                    bool onEdge = false;
                    for (int k = 0; k < cn && !onEdge; k++)
                        if (IsPointOnSegment(mid, contourPts[k], contourPts[(k + 1) % cn], MeshTol.OnSegment))
                            onEdge = true;
                    if (!onEdge && !IsPointInPolygon(mid, contourPts)) { trimmed = true; continue; }

                    result.Add(new Point2d[] { p0, p1 });
                    keptParts++;
                }

                if (keptParts == 0) removedOutside++;
                else if (trimmed) clippedCount++;
            }
            return result;
        }

        // Зеркально ClipSegmentsToContour, но для пилонов: внутренность пилона пуста,
        // поэтому отрезок режется точками пересечения со сторонами всех пилонов, части
        // с серединой строго внутри какого-либо пилона отбрасываются. Части, лежащие
        // на самих сторонах пилона, остаются (это грани, врезанные в сетку).
        private List<Point2d[]> ClipSegmentsOutsideColumns(
            List<Point2d[]> segments,
            List<List<Point2d>> columnPolys,
            out int clippedCount,
            out int removedInside)
        {
            clippedCount = 0;
            removedInside = 0;
            if (columnPolys.Count == 0) return segments;
            var result = new List<Point2d[]>();

            foreach (var seg in segments)
            {
                Point2d a = seg[0], b = seg[1];
                double len = a.GetDistanceTo(b);
                if (len < 1e-9) continue;

                var ts = new List<double> { 0.0, 1.0 };
                foreach (var col in columnPolys)
                {
                    int cn = col.Count;
                    for (int i = 0; i < cn; i++)
                    {
                        Point2d c1 = col[i];
                        Point2d c2 = col[(i + 1) % cn];
                        if (!SegmentsIntersect(a, b, c1, c2)) continue;

                        double denom = (b.X - a.X) * (c2.Y - c1.Y) - (b.Y - a.Y) * (c2.X - c1.X);
                        if (Math.Abs(denom) < 1e-12) continue;
                        double t = ((c1.X - a.X) * (c2.Y - c1.Y) - (c1.Y - a.Y) * (c2.X - c1.X)) / denom;
                        if (t > 1e-9 && t < 1.0 - 1e-9) ts.Add(t);
                    }
                }
                ts.Sort();

                int keptParts = 0;
                bool trimmed = false;
                for (int i = 0; i + 1 < ts.Count; i++)
                {
                    double t0 = ts[i], t1 = ts[i + 1];
                    if ((t1 - t0) * len < MeshTol.MinPiece) { trimmed = true; continue; }

                    Point2d p0 = new Point2d(a.X + (b.X - a.X) * t0, a.Y + (b.Y - a.Y) * t0);
                    Point2d p1 = new Point2d(a.X + (b.X - a.X) * t1, a.Y + (b.Y - a.Y) * t1);
                    Point2d mid = new Point2d((p0.X + p1.X) / 2.0, (p0.Y + p1.Y) / 2.0);

                    bool insideColumn = false;
                    foreach (var col in columnPolys)
                    {
                        bool onEdge = false;
                        int cn = col.Count;
                        for (int k = 0; k < cn && !onEdge; k++)
                            if (IsPointOnSegment(mid, col[k], col[(k + 1) % cn], MeshTol.OnSegment))
                                onEdge = true;
                        if (!onEdge && IsPointInPolygon(mid, col)) { insideColumn = true; break; }
                    }
                    if (insideColumn) { trimmed = true; continue; }

                    result.Add(new Point2d[] { p0, p1 });
                    keptParts++;
                }

                if (keptParts == 0) removedInside++;
                else if (trimmed) clippedCount++;
            }
            return result;
        }

        // Совпадающие отрезки (общее ребро двух соседних ячеек/треугольников) не должны
        // попадать в DXF дважды: ЛИРА-САПР требует, чтобы отрезки на плане не накладывались.
        private List<Point2d[]> DeduplicateSegments(List<Point2d[]> segments)
        {
            var ni = new NodeIndex();
            var seen = new HashSet<long>();
            var result = new List<Point2d[]>();
            foreach (var seg in segments)
            {
                int ia = ni.GetNode(seg[0]);
                int ib = ni.GetNode(seg[1]);
                if (ia == ib) continue; // концы слились по допуску — отрезка нет
                if (seen.Add(EdgePairKey(ia, ib)))
                    result.Add(seg);
            }
            return result;
        }


        private bool SegmentLiesOnContour(Point2d a, Point2d b, List<Point2d> contour, double eps = MeshTol.OnSegment)
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

            // Ключ прямой: направление и её смещение от начала координат, квантованные
            // целыми. Целые числа не зависят от локали (прежний ToString("F6") на
            // русской локали давал "0,123456"), а квант сохранён прежним.
            string key = (long)Math.Round(nx * 1e6) + "|" + (long)Math.Round(ny * 1e6)
                + "|" + (long)Math.Round(c * 10.0);

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
                    if (ts.Count > 0 && br.Key - ts[ts.Count - 1] < MeshTol.NodeMerge) continue;
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
                    if (left.Count >= 3 && Math.Abs(PolygonArea(left)) > MeshTol.MinArea) { next.Add(left); added = true; }
                    if (right.Count >= 3 && Math.Abs(PolygonArea(right)) > MeshTol.MinArea) { next.Add(right); added = true; }
                    if (!added) next.Add(piece);
                }

                pieces = next;
            }

            return pieces;
        }


    }
}
