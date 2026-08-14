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
        // ПРЕДВАРИТЕЛЬНАЯ ПРОВЕРКА ЧЕРТЕЖА.
        //
        // Те же требования к входу, что проверяет MESHQUADMESH, но собранные в один
        // проход и без остановки на первом нарушении. В конвейере проверки стоят по
        // ходу дела и валят команду по одной: «контур не замкнут» → правка → запуск →
        // «стена вне плиты» → правка → запуск → «дуга в контуре». На реальном плане
        // каждый круг стоит несколько минут. MESHCHECK выдаёт весь список сразу.
        //
        // Команда НИЧЕГО не строит и не переносит между слоями: единственное, что она
        // добавляет в чертёж, — круги-маркеры в слое ПРОБЛЕМА (результат проверки).
        [CommandMethod("MESHCHECK")]
        public void CheckDrawingCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHCHECK");
            Database db = doc.Database;

            PromptEntityOptions peo = new PromptEntityOptions("\nВыберите контур плиты (полилинию): ");
            peo.SetRejectMessage("\nМожно выбрать только полилинию (LWPOLYLINE).");
            peo.AddAllowedClass(typeof(Polyline), false);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            PromptDoubleOptions pdo = new PromptDoubleOptions("\nШаг сетки для оценки (Enter — 300): ");
            pdo.DefaultValue = 300.0;
            pdo.AllowNegative = false;
            pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK && pdr.Status != PromptStatus.None) return;
            double cellSize = pdr.Status == PromptStatus.OK ? pdr.Value : 300.0;

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

                var errors = new List<string>();    // построение не пройдёт или даст мусор
                var warnings = new List<string>();  // строить можно, но посмотреть стоит
                var marks = new List<Point2d>();    // куда ставить круги ПРОБЛЕМА

                EraseMarksOnLayer(tr, db, ProblemLayerName);

                // ---- 1. Контур плиты -------------------------------------------------
                var rawVerts = GetPolylineVertices(pline);

                if (!pline.Closed)
                {
                    errors.Add("контур плиты не замкнут (замкните полилинию: свойство Closed или команда PEDIT → Замкнуть)");
                    marks.Add(rawVerts[0]);
                    marks.Add(rawVerts[rawVerts.Count - 1]);
                }

                if (PolylineHasArcs(pline))
                {
                    int arcs = 0;
                    for (int i = 0; i < pline.NumberOfVertices; i++)
                        if (pline.GetSegmentType(i) == SegmentType.Arc)
                        {
                            arcs++;
                            if (marks.Count < 200) marks.Add(rawVerts[i % rawVerts.Count]);
                        }
                    errors.Add($"в контуре плиты дуг: {arcs} — замените дуги хордами (ЛИРА дуги не принимает)");
                }

                if (!IsPolylineFlatXY(pline))
                    warnings.Add("контур плиты лежит не в плоскости XY — сетка будет построена по проекции на XY");

                var contourPts = CleanupPolygon(rawVerts);
                double contourArea = Math.Abs(PolygonArea(contourPts));
                bool contourOk = contourPts.Count >= 3 && contourArea > MeshTol.MinArea;
                if (!contourOk)
                {
                    errors.Add("контур плиты вырожден: меньше трёх несовпадающих вершин или нулевая площадь");
                }
                else
                {
                    EnsureCcw(contourPts);

                    // Самопересечения и кривые углы ищутся теми же функциями, что и в
                    // ValidateContour (Geometry.cs) — разница только в реакции: там
                    // построение останавливается на первом, здесь показываются все.
                    var selfInts = FindSelfIntersections(contourPts);
                    if (selfInts.Count > 0)
                    {
                        errors.Add($"контур плиты самопересекается: пересечений сторон {selfInts.Count}");
                        marks.AddRange(selfInts);
                    }

                    List<double> cornerAngles;
                    var badIdx = FindNonRightCorners(contourPts, out cornerAngles);
                    if (badIdx.Count > 0)
                    {
                        var cornerPts = new List<Point2d>();
                        foreach (int i in badIdx) cornerPts.Add(contourPts[i]);
                        warnings.Add($"углов контура, отличных от 90°: {badIdx.Count} — отмечены в слое {AngleMarkLayerName}");
                        EraseMarksOnLayer(tr, db, AngleMarkLayerName);
                        DrawMarkCircles(tr, db, AngleMarkLayerName, cornerPts, AngleMarkRadius);
                    }
                }

                // ---- 2. Один проход по чертежу --------------------------------------
                var wallSegs = new List<Point2d[]>();
                var columnPolys = new List<List<Point2d>>();
                var columnCenters = new List<Point2d>();
                var holePolys = new List<List<Point2d>>();
                var doorSegs = new List<Point2d[]>();
                int holeOpen = 0, doorsWithoutHeight = 0, gridLines = 0;
                var foreignLayers = new Dictionary<string, int>();

                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    string layer = ent.Layer ?? "";

                    if (ent is DBPoint dbp && IsColumnLayer(layer))
                    {
                        columnCenters.Add(new Point2d(dbp.Position.X, dbp.Position.Y));
                        continue;
                    }

                    if (IsColumnLayer(layer))
                    {
                        if (ent is Polyline cpl && cpl.Closed)
                        {
                            var cv = GetPolylineVertices(cpl);
                            if (cv.Count >= 3) { EnsureCcw(cv); columnPolys.Add(cv); }
                        }
                        continue;
                    }

                    if (layer == HoleLayerName)
                    {
                        if (ent is Polyline hpl)
                        {
                            if (!hpl.Closed) holeOpen++;
                            else
                            {
                                var hv = GetPolylineVertices(hpl);
                                if (hv.Count >= 3) { EnsureCcw(hv); holePolys.Add(hv); }
                            }
                        }
                        continue;
                    }

                    if (IsDoorLayer(layer))
                    {
                        double dh;
                        if (!TryParseLayerHeight(layer, out dh)) doorsWithoutHeight++;
                        AppendSegments(ent, doorSegs);
                        continue;
                    }

                    if (IsWallLayer(layer))
                    {
                        AppendSegments(ent, wallSegs);
                        continue;
                    }

                    if (layer == TriangulationLayerName) { gridLines++; continue; }
                    if (IsSlabLayer(layer) || IsMarkLayer(layer) || layer == DoorMarkLayerName) continue;
                    if (id == per.ObjectId) continue; // сам выбранный контур

                    int cnt;
                    foreignLayers.TryGetValue(layer, out cnt);
                    foreignLayers[layer] = cnt + 1;
                }

                // Дуги и неплоские полилинии по всему чертежу — своим сообщением.
                WarnBadPolylines(tr, db, ed);

                // ---- 3. Всё внутри плиты --------------------------------------------
                if (contourOk)
                {
                    int wallsOut = 0;
                    foreach (var w in wallSegs)
                        if (!IsSegmentInsideContour(w[0], w[1], contourPts))
                        {
                            wallsOut++;
                            marks.Add(new Point2d((w[0].X + w[1].X) / 2.0, (w[0].Y + w[1].Y) / 2.0));
                        }
                    if (wallsOut > 0)
                        errors.Add($"стен (осей) вне контура плиты: {wallsOut} — построение будет остановлено");

                    int colsOut = 0;
                    foreach (var c in columnPolys)
                        if (!IsPolygonInsideContour(c, contourPts)) { colsOut++; marks.Add(PolygonCentroid(c)); }
                    if (colsOut > 0)
                        errors.Add($"пилонов вне контура плиты: {colsOut} — построение будет остановлено");

                    int holesOut = 0;
                    foreach (var h in holePolys)
                        if (!IsPolygonInsideContour(h, contourPts)) { holesOut++; marks.Add(PolygonCentroid(h)); }
                    if (holesOut > 0)
                        errors.Add($"отверстий вне контура плиты: {holesOut} — построение будет остановлено");
                }

                if (holeOpen > 0)
                    errors.Add($"незамкнутых полилиний на слое {HoleLayerName}: {holeOpen} — такой проём будет молча пропущен");

                // ---- 4. Двери на осях стен ------------------------------------------
                int doorsOffAxis = 0;
                foreach (var d in doorSegs)
                {
                    Point2d mid = new Point2d((d[0].X + d[1].X) / 2.0, (d[0].Y + d[1].Y) / 2.0);
                    bool onAxis = false;
                    foreach (var w in wallSegs)
                    {
                        if (!IsPointOnSegment(d[0], w[0], w[1], MeshTol.DoorOnAxis)) continue;
                        if (!IsPointOnSegment(d[1], w[0], w[1], MeshTol.DoorOnAxis)) continue;
                        if (!IsPointOnSegment(mid, w[0], w[1], MeshTol.DoorOnAxis)) continue;
                        onAxis = true;
                        break;
                    }
                    if (!onAxis) { doorsOffAxis++; marks.Add(mid); }
                }
                if (doorsOffAxis > 0)
                    errors.Add($"дверных проёмов не на оси стены: {doorsOffAxis} — такой проём при экспорте не вырежется (кусок стены ищется по середине с допуском {MeshTol.DoorOnAxis:0.#} мм)");
                if (doorsWithoutHeight > 0)
                    warnings.Add($"дверей без высоты в имени слоя ({DoorLayerPrefix}H-...): {doorsWithoutHeight} — будет принято 2100 мм");

                // ---- 5. Пилоны без точки центра -------------------------------------
                int colsNoCenter = 0;
                foreach (var c in columnPolys)
                {
                    bool has = false;
                    foreach (var cc in columnCenters)
                        if (IsPointInPolygon(cc, c)) { has = true; break; }
                    if (!has) { colsNoCenter++; marks.Add(PolygonCentroid(c)); }
                }
                if (colsNoCenter > 0)
                    warnings.Add($"контуров пилонов без точки центра: {colsNoCenter} — запустите MESHCOLUMNCROSS (пластина) или MESHCOLUMNSBAR (стержень)");

                // ---- 6. Задвоенные оси ----------------------------------------------
                int dupWalls = DuplicateSegmentCount(wallSegs);
                if (dupWalls > 0)
                    errors.Add($"совпадающих осей стен/пилонов: {dupWalls} — в ЛИРЕ это наложенные друг на друга элементы");
                int dupDoors = DuplicateSegmentCount(doorSegs);
                if (dupDoors > 0)
                    warnings.Add($"совпадающих дверных отрезков: {dupDoors}");

                // ---- 7. Чужие слои ---------------------------------------------------
                if (foreignLayers.Count > 0)
                {
                    int total = 0;
                    foreach (var kv in foreignLayers) total += kv.Value;
                    var top = new List<KeyValuePair<string, int>>(foreignLayers);
                    top.Sort((a, b) => b.Value.CompareTo(a.Value));
                    var names = new List<string>();
                    for (int i = 0; i < Math.Min(5, top.Count); i++) names.Add($"{top[i].Key} ({top[i].Value})");
                    warnings.Add($"объектов на слоях, которые построение не увидит: {total} — {string.Join(", ", names)}{(top.Count > 5 ? ", …" : "")}. Если это стены или плита — разложите их командой MESHLAYERS");
                }

                // ---- 8. Оценка объёма сетки ------------------------------------------
                if (contourOk)
                {
                    double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                    foreach (var p in contourPts)
                    {
                        if (p.X < minX) minX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y > maxY) maxY = p.Y;
                    }
                    double w = maxX - minX, h = maxY - minY;
                    double cells = Math.Ceiling(w / cellSize) * Math.Ceiling(h / cellSize);
                    ed.WriteMessage($"\nПлита: {contourArea * 1e-6:0.###} м², габарит {w:0} x {h:0} мм; при шаге {cellSize:0.#} мм ячеек ≈ {cells:0}; стен: {wallSegs.Count}, пилонов: {columnPolys.Count}, отверстий: {holePolys.Count}, дверей: {doorSegs.Count}, линий готовой сетки: {gridLines}\n");

                    if (cellSize > Math.Min(w, h) / 2.0)
                        errors.Add($"шаг {cellSize:0.#} мм больше половины плиты — проверьте единицы чертежа (шаг задаётся в мм)");
                    else if (cells > 200000)
                        warnings.Add($"при шаге {cellSize:0.#} мм получится ≈{cells:0} ячеек — построение будет очень долгим, возьмите шаг крупнее");
                }

                // ---- Итог -------------------------------------------------------------
                if (marks.Count > 0)
                    DrawMarkCircles(tr, db, ProblemLayerName, marks, ProblemMarkRadius);

                ed.WriteMessage("\n=== ПРОВЕРКА ЧЕРТЕЖА ===\n");
                foreach (var e in errors) ed.WriteMessage($"  ОШИБКА: {e}\n");
                foreach (var w2 in warnings) ed.WriteMessage($"  внимание: {w2}\n");

                if (errors.Count == 0 && warnings.Count == 0)
                    ed.WriteMessage("Замечаний нет — можно строить сетку (MESHQUADMESH).\n");
                else
                    ed.WriteMessage($"Итого ошибок: {errors.Count}, предупреждений: {warnings.Count}" +
                        (marks.Count > 0 ? $"; мест на чертеже отмечено кругами в слое {ProblemLayerName}: {marks.Count}" : "") +
                        (errors.Count == 0 ? ". Ошибок нет — строить можно.\n" : ". Постройте сетку после устранения ошибок.\n"));

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHCHECK: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // Отрезки объекта (Line или Polyline) в общий список — чтение чертежа
        // в MESHCHECK идёт одним проходом, и разбор одинаков для стен и дверей.
        private void AppendSegments(Entity ent, List<Point2d[]> target)
        {
            if (ent is Line ln)
            {
                target.Add(new Point2d[]
                {
                    new Point2d(ln.StartPoint.X, ln.StartPoint.Y),
                    new Point2d(ln.EndPoint.X, ln.EndPoint.Y)
                });
                return;
            }

            if (ent is Polyline pl)
            {
                var verts = GetPolylineVertices(pl);
                int n = verts.Count;
                int segCount = pl.Closed ? n : n - 1;
                for (int i = 0; i < segCount; i++)
                    target.Add(new Point2d[] { verts[i], verts[(i + 1) % n] });
            }
        }

        // Сколько отрезков лежит поверх уже встреченного (совпадение концов с допуском
        // слияния узлов). Ноль длины считается совпадением с самим собой не считается —
        // вырожденные отрезки пропускаются.
        private int DuplicateSegmentCount(List<Point2d[]> segments)
        {
            var ni = new NodeIndex();
            var seen = new HashSet<long>();
            int dup = 0;
            foreach (var s in segments)
            {
                if (s[0].GetDistanceTo(s[1]) < MeshTol.NodeMerge) continue;
                int a = ni.GetNode(s[0]), b = ni.GetNode(s[1]);
                if (!seen.Add(EdgePairKey(a, b))) dup++;
            }
            return dup;
        }
    }
}
