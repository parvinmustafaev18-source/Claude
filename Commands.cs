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
        [CommandMethod("MESHHELLO")]
        public void HelloCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHHELLO");
            ed.WriteMessage("\nПривет! Плагин загружен и работает.\n");
        }


        [CommandMethod("MESHLAYERS")]
        public void CreateLayersCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHLAYERS");
            Database db = doc.Database;

            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите отрезки сетки и контур (Line + Polyline): ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE") });
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            PromptDoubleOptions pdo = new PromptDoubleOptions("\nТолщина плиты: ");
            pdo.AllowNegative = false;
            pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double slabThickness = pdr.Value;

            string slabLayerName = SlabLayerPrefix + $"H-{slabThickness:0.###})";
            string beamLayerName = TriangulationLayerName;

            var rnd = new Random();

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var usedColors = GetUsedLayerColors(db, tr);
                short slabColor = PickRandomColor(rnd, usedColors);
                short beamColor = PickRandomColor(rnd, usedColors);

                EnsureLayer(db, tr, slabLayerName, slabColor);
                EnsureLayer(db, tr, beamLayerName, beamColor);

                int slabCount = 0, beamCount = 0;
                int skippedWalls = 0;
                int skippedService = 0;

                // Слои, которые MESHLAYERS не трогает: стены/пилоны и служебные слои
                // плагина (проёмы MESH_HOLES, маркеры MESH_*, ПРОБЛЕМА, ПЛОХИЕ). Без этого
                // рамочный выбор уводил полилинии проёмов с MESH_HOLES → экспорт не видел
                // отверстия (holePolys=0) и зашивал их веером КЭ 42.
                bool KeepLayer(string layer) =>
                    IsWallLayer(layer) || IsColumnLayer(layer)
                    || IsDoorLayer(layer) || layer == DoorMarkLayerName
                    || layer == HoleLayerName || IsMarkLayer(layer);

                foreach (SelectedObject so in psr.Value)
                {
                    Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;

                    if (KeepLayer(ent.Layer))
                    {
                        if (IsWallLayer(ent.Layer) || IsColumnLayer(ent.Layer))
                            skippedWalls++;
                        else
                            skippedService++;
                        continue;
                    }

                    if (ent is Polyline)
                    {
                        ent.Layer = slabLayerName;
                        slabCount++;
                    }
                    else if (ent is Line)
                    {
                        ent.Layer = beamLayerName;
                        beamCount++;
                    }
                }

                // Контрольный проход: отрезки обязаны оказаться в слое триангуляции,
                // полилиния — в слое фундаментной плиты. Несовпадения исправляются.
                int fixedCount = 0;
                foreach (SelectedObject so in psr.Value)
                {
                    Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    if (KeepLayer(ent.Layer)) continue;

                    string expected = null;
                    if (ent is Polyline) expected = slabLayerName;
                    else if (ent is Line) expected = beamLayerName;

                    if (expected != null && ent.Layer != expected)
                    {
                        ent.Layer = expected;
                        fixedCount++;
                    }
                }

                ed.WriteMessage($"\nПлита ({slabLayerName}): {slabCount}, триангуляция ({beamLayerName}): {beamCount}, пропущено стен: {skippedWalls}, пропущено служебных (MESH_HOLES/маркеры): {skippedService}, исправлено проверкой: {fixedCount}\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHLAYERS: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        [CommandMethod("MESHWALLS")]
        public void CreateWallsLayerCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHWALLS");
            Database db = doc.Database;

            PromptDoubleOptions pdo = new PromptDoubleOptions("\nТолщина стены: ");
            pdo.AllowNegative = false;
            pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double wallThickness = pdr.Value;

            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите стены (Line + Polyline): ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE") });
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            string wallLayerName = WallLayerPrefix + $"{wallThickness:0.###})";

            var rnd = new Random();

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var usedColors = GetUsedLayerColors(db, tr);
                short wallColor = PickRandomColor(rnd, usedColors);

                EnsureLayer(db, tr, wallLayerName, wallColor);

                int wallCount = 0;

                foreach (SelectedObject so in psr.Value)
                {
                    Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;

                    ent.Layer = wallLayerName;
                    wallCount++;
                }

                ed.WriteMessage($"\nСтены ({wallLayerName}): {wallCount}\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHWALLS: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        [CommandMethod("MESHDOORS")]
        public void CreateDoorsLayerCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHDOORS");
            Database db = doc.Database;

            // Высота двери (в мм) — уходит в имя слоя WALL_DOORS(H-<высота>).
            PromptDoubleOptions pdo = new PromptDoubleOptions("\nВысота дверного проёма, мм: ");
            pdo.DefaultValue = 2100.0;
            pdo.AllowNegative = false;
            pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double doorHeight = pdr.Value;

            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите отрезки дверных проёмов (нарисованные поверх оси стены): ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE") });
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            string doorLayerName = DoorLayerPrefix + $"H-{doorHeight:0.###})";

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureLayer(db, tr, doorLayerName, 30); // оранжевый — двери заметны

                // Середины дверных отрезков — по ним ставятся квадраты-обозначения.
                var doorMids = new List<Point2d>();
                var doorAxes = new List<Point2d>();

                int doorCount = 0;
                foreach (SelectedObject so in psr.Value)
                {
                    Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    ent.Layer = doorLayerName;
                    doorCount++;

                    // Полилиния из нескольких сегментов — это несколько проёмов,
                    // квадрат ставится на каждый (как их и читает экспорт).
                    if (ent is Line ln)
                    {
                        CollectDoorMark(
                            new Point2d(ln.StartPoint.X, ln.StartPoint.Y),
                            new Point2d(ln.EndPoint.X, ln.EndPoint.Y), doorMids, doorAxes);
                    }
                    else if (ent is Polyline pl)
                    {
                        var verts = GetPolylineVertices(pl);
                        int n = verts.Count;
                        int segCount = pl.Closed ? n : n - 1;
                        for (int i = 0; i < segCount; i++)
                            CollectDoorMark(verts[i], verts[(i + 1) % n], doorMids, doorAxes);
                    }
                }

                int marks = DrawDoorMarks(tr, db, doorMids, doorAxes);

                ed.WriteMessage($"\nДверных проёмов ({doorLayerName}): {doorCount}, обозначений {DoorMarkSize:0.#}x{DoorMarkSize:0.#} в слое {DoorMarkLayerName}: {marks}. Отрезок должен лежать точно на оси стены; экспорт вырежет стену от пола до {doorHeight:0.#} мм, выше оставит перемычку. Квадраты — только для чертежа, в ЛИРУ не экспортируются.\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHDOORS: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // Осевые линии стен из их контуров: для замкнутого прямоугольного контура
        // толщина = короткая пара сторон, ось = линия между серединами торцов.
        // Ось попадает в слой WALLS(H-толщина) — дальше её видят MESHQUADMESH и
        // MESHEXPORTTXT как обычную стену. Исходный контур не изменяется.
        // Непрямоугольные контуры (Г-образные и т.п.) пропускаются — для них ось
        // строится вручную и оформляется через MESHWALLS.
        [CommandMethod("MESHWALLAXIS")]
        public void WallAxisCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHWALLAXIS");
            Database db = doc.Database;

            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите контуры стен (замкнутые полилинии): ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            var rnd = new Random();

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var usedColors = GetUsedLayerColors(db, tr);
                var thicknessLayers = new Dictionary<string, int>();
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                int axisCount = 0, skippedOpen = 0, skippedComplex = 0, skippedService = 0;

                foreach (SelectedObject so in psr.Value)
                {
                    Polyline pl = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;

                    // Служебные слои плагина игнорируются, даже замкнутые контуры:
                    // рамочное выделение всего плана не превратит плиту и пилоны в стены.
                    if (IsServiceLayer(pl.Layer)) { skippedService++; continue; }

                    if (!pl.Closed) { skippedOpen++; continue; }

                    var pts = RemoveCollinearVertices(CleanupPolygon(GetPolylineVertices(pl)));
                    if (pts.Count != 4) { skippedComplex++; continue; }

                    // Стороны 0-1, 1-2, 2-3, 3-0; противоположные пары (0,2) и (1,3)
                    double[] len = new double[4];
                    for (int i = 0; i < 4; i++)
                        len[i] = pts[i].GetDistanceTo(pts[(i + 1) % 4]);

                    double pairA = (len[0] + len[2]) / 2.0;
                    double pairB = (len[1] + len[3]) / 2.0;
                    int e1, e2;
                    double thickness, length;
                    if (pairA <= pairB) { e1 = 0; e2 = 2; thickness = pairA; length = pairB; }
                    else { e1 = 1; e2 = 3; thickness = pairB; length = pairA; }

                    // Торцы должны быть примерно равны, иначе это трапеция, а не стена
                    if (Math.Abs(len[e1] - len[e2]) > 0.2 * thickness + 1.0) { skippedComplex++; continue; }
                    if (thickness < 1.0 || length < thickness) { skippedComplex++; continue; }

                    Point2d m1 = new Point2d((pts[e1].X + pts[(e1 + 1) % 4].X) / 2.0,
                                             (pts[e1].Y + pts[(e1 + 1) % 4].Y) / 2.0);
                    Point2d m2 = new Point2d((pts[e2].X + pts[(e2 + 1) % 4].X) / 2.0,
                                             (pts[e2].Y + pts[(e2 + 1) % 4].Y) / 2.0);

                    // Толщина округляется до 10 мм: слоёв вида WALLS(H-201) или
                    // WALLS(H-205) быть не должно — только 200, 210, 220 и т.д.
                    double t = Math.Round(thickness / 10.0) * 10.0;
                    string layerName = WallLayerPrefix + $"{t:0.###})";
                    if (!thicknessLayers.ContainsKey(layerName))
                    {
                        EnsureLayer(db, tr, layerName, PickRandomColor(rnd, usedColors));
                        thicknessLayers[layerName] = 0;
                    }

                    Line axis = new Line(new Point3d(m1.X, m1.Y, 0), new Point3d(m2.X, m2.Y, 0));
                    axis.Layer = layerName;
                    ms.AppendEntity(axis);
                    tr.AddNewlyCreatedDBObject(axis, true);

                    thicknessLayers[layerName]++;
                    axisCount++;
                }

                ed.WriteMessage($"\nОсевых линий построено: {axisCount}, пропущено незамкнутых: {skippedOpen}, непрямоугольных (ось вручную + MESHWALLS): {skippedComplex}" +
                    (skippedService > 0 ? $", пропущено в служебных слоях плагина: {skippedService}" : "") + "\n");
                foreach (var kv in thicknessLayers)
                    ed.WriteMessage($"  {kv.Key}: {kv.Value}\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHWALLAXIS: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // Зачистка чертежа: удаляет из текущего пространства все объекты вне служебных
        // слоёв плагина (FOUNDATION_SLABS, WALLS(H-...), COLUMNS, LINE_TRIANGULATION) —
        // исходную подоснову, контуры стен и прочий мусор. Перед удалением показывает
        // количество и просит подтверждение; одно Ctrl+Z отменяет всю зачистку.
        [CommandMethod("MESHCLEAN")]
        public void CleanDrawingCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHCLEAN");
            Database db = doc.Database;

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Заблокированные слои: объекты в них удалить нельзя — пропускаются
                var lockedLayers = new HashSet<string>();
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in lt)
                {
                    LayerTableRecord ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                    if (ltr.IsLocked) lockedLayers.Add(ltr.Name);
                }

                var toErase = new List<ObjectId>();
                int skippedLocked = 0;

                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    if (IsServiceLayer(ent.Layer)) continue;
                    if (lockedLayers.Contains(ent.Layer)) { skippedLocked++; continue; }
                    toErase.Add(id);
                }

                if (toErase.Count == 0)
                {
                    ed.WriteMessage("\nУдалять нечего: вне служебных слоёв объектов нет" +
                        (skippedLocked > 0 ? $" (в заблокированных слоях пропущено: {skippedLocked})" : "") + ".\n");
                    return;
                }

                PromptKeywordOptions pko = new PromptKeywordOptions(
                    $"\nБудет удалено объектов вне служебных слоёв: {toErase.Count}. Удалить?");
                pko.Keywords.Add("Yes");
                pko.Keywords.Add("No");
                pko.Keywords.Default = "No";
                PromptResult pr = ed.GetKeywords(pko);
                bool confirmed = pr.Status == PromptStatus.OK && pr.StringResult == "Yes";
                if (!confirmed)
                {
                    ed.WriteMessage("\nЗачистка отменена.\n");
                    return;
                }

                foreach (ObjectId id in toErase)
                {
                    Entity ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                    ent.Erase();
                }

                ed.WriteMessage($"\nУдалено объектов: {toErase.Count}" +
                    (skippedLocked > 0 ? $", пропущено в заблокированных слоях: {skippedLocked}" : "") +
                    ". Отмена — Ctrl+Z.\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHCLEAN: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // Дотягивание осей стен друг до друга (ручной режим): пользователь выбирает
        // отрезки, программа сама решает — непараллельные продлить до точки пересечения
        // (только удлинение, укорачивания нет), коллинеарные из одного слоя слить в один
        // отрезок. Дотягивание ограничено максимальным зазором, чтобы случайный выбор
        // не продлил ось через весь план.
        [CommandMethod("MESHWALLJOIN")]
        public void JoinWallAxesCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHWALLJOIN");
            Database db = doc.Database;

            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите оси стен (отрезки): ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LINE") });
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            PromptDoubleOptions pdoGap = new PromptDoubleOptions("\nМаксимальный зазор для дотягивания, мм: ");
            pdoGap.DefaultValue = 500.0;
            pdoGap.AllowNegative = false;
            pdoGap.AllowZero = false;
            PromptDoubleResult pdrGap = ed.GetDouble(pdoGap);
            if (pdrGap.Status != PromptStatus.OK) return;
            double maxGap = pdrGap.Value;

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var ids = new List<ObjectId>();
                var segs = new List<Point2d[]>();
                var layers = new List<string>();
                var alive = new List<bool>();
                var changed = new List<bool>();

                foreach (SelectedObject so in psr.Value)
                {
                    Line line = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Line;
                    if (line == null) continue;
                    ids.Add(so.ObjectId);
                    segs.Add(new Point2d[]
                    {
                        new Point2d(line.StartPoint.X, line.StartPoint.Y),
                        new Point2d(line.EndPoint.X, line.EndPoint.Y)
                    });
                    layers.Add(line.Layer);
                    alive.Add(true);
                    changed.Add(false);
                }

                if (ids.Count < 2)
                {
                    ed.WriteMessage("\nНужно минимум два отрезка.\n");
                    return;
                }

                int mergedCount = 0, extendedCount = 0;
                bool anyChange = true;

                for (int pass = 0; pass < 5 && anyChange; pass++)
                {
                    anyChange = false;

                    // Слияние коллинеарных отрезков одного слоя (перекрытие или зазор ≤ maxGap)
                    for (int i = 0; i < segs.Count; i++)
                    {
                        if (!alive[i]) continue;
                        Point2d a0 = segs[i][0], a1 = segs[i][1];
                        double lenA = a0.GetDistanceTo(a1);
                        if (lenA < 1e-6) continue;
                        double ux = (a1.X - a0.X) / lenA, uy = (a1.Y - a0.Y) / lenA;

                        for (int j = i + 1; j < segs.Count; j++)
                        {
                            if (!alive[j] || layers[i] != layers[j]) continue;
                            Point2d b0 = segs[j][0], b1 = segs[j][1];
                            double lenB = b0.GetDistanceTo(b1);
                            if (lenB < 1e-6) continue;
                            double vx = (b1.X - b0.X) / lenB, vy = (b1.Y - b0.Y) / lenB;

                            if (Math.Abs(ux * vy - uy * vx) > 1e-3) continue; // не параллельны

                            // Боковое смещение концов j от прямой i — не более 1 мм
                            double d0 = Math.Abs((b0.X - a0.X) * uy - (b0.Y - a0.Y) * ux);
                            double d1 = Math.Abs((b1.X - a0.X) * uy - (b1.Y - a0.Y) * ux);
                            if (d0 > 1.0 || d1 > 1.0) continue;

                            double tb0 = (b0.X - a0.X) * ux + (b0.Y - a0.Y) * uy;
                            double tb1 = (b1.X - a0.X) * ux + (b1.Y - a0.Y) * uy;
                            if (tb0 > tb1) { double tt = tb0; tb0 = tb1; tb1 = tt; }

                            double gap = Math.Max(Math.Max(tb0 - lenA, -tb1), 0.0);
                            if (gap > maxGap) continue;

                            double lo = Math.Min(0.0, tb0), hi = Math.Max(lenA, tb1);
                            segs[i] = new Point2d[]
                            {
                                new Point2d(a0.X + ux * lo, a0.Y + uy * lo),
                                new Point2d(a0.X + ux * hi, a0.Y + uy * hi)
                            };
                            changed[i] = true;
                            alive[j] = false;
                            mergedCount++;
                            anyChange = true;

                            a0 = segs[i][0]; a1 = segs[i][1];
                            lenA = a0.GetDistanceTo(a1);
                        }
                    }

                    // Продление непараллельных отрезков до точки пересечения их прямых
                    for (int i = 0; i < segs.Count; i++)
                    {
                        if (!alive[i]) continue;
                        for (int j = i + 1; j < segs.Count; j++)
                        {
                            if (!alive[j]) continue;

                            Point2d p0 = segs[i][0], p1 = segs[i][1];
                            Point2d q0 = segs[j][0], q1 = segs[j][1];
                            double lenI = p0.GetDistanceTo(p1), lenJ = q0.GetDistanceTo(q1);
                            if (lenI < 1e-6 || lenJ < 1e-6) continue;

                            double cross = ((p1.X - p0.X) / lenI) * ((q1.Y - q0.Y) / lenJ)
                                         - ((p1.Y - p0.Y) / lenI) * ((q1.X - q0.X) / lenJ);
                            if (Math.Abs(cross) < 1e-3) continue; // параллельны — не дотягиваем

                            Point2d ip = LineIntersection(p0, p1, q0, q1);

                            foreach (int k in new int[] { i, j })
                            {
                                Point2d s0 = segs[k][0], s1 = segs[k][1];
                                double lenK = s0.GetDistanceTo(s1);
                                double ex = (s1.X - s0.X) / lenK, ey = (s1.Y - s0.Y) / lenK;
                                double t = (ip.X - s0.X) * ex + (ip.Y - s0.Y) * ey;

                                if (t < -1e-6 && -t <= maxGap)
                                {
                                    segs[k] = new Point2d[] { ip, s1 };
                                    changed[k] = true;
                                    extendedCount++;
                                    anyChange = true;
                                }
                                else if (t > lenK + 1e-6 && t - lenK <= maxGap)
                                {
                                    segs[k] = new Point2d[] { s0, ip };
                                    changed[k] = true;
                                    extendedCount++;
                                    anyChange = true;
                                }
                            }
                        }
                    }
                }

                // Запись результата: слитые отрезки удаляются, изменённые переписываются
                for (int i = 0; i < ids.Count; i++)
                {
                    if (alive[i] && !changed[i]) continue;
                    Line line = (Line)tr.GetObject(ids[i], OpenMode.ForWrite);
                    if (!alive[i])
                    {
                        line.Erase();
                    }
                    else
                    {
                        line.StartPoint = new Point3d(segs[i][0].X, segs[i][0].Y, 0);
                        line.EndPoint = new Point3d(segs[i][1].X, segs[i][1].Y, 0);
                    }
                }

                ed.WriteMessage($"\nПродлено концов до пересечения: {extendedCount}, слито коллинеарных отрезков: {mergedCount}\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHWALLJOIN: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // СТАРЫЙ режим пилонов (стержень КЭ 10 при экспорте): контур в слой
        // COLUMNS(SEC-...) + точка центра, внутренность пуста. Оставлен как
        // запасной вариант; основной путь теперь MESHCOLUMNCROSS — пилон
        // крестом пластин-стен.
        [CommandMethod("MESHCOLUMNSBAR")]
        public void CreateColumnsLayerCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHCOLUMNSBAR");
            Database db = doc.Database;

            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите пилоны (замкнутые полилинии): ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            var rnd = new Random();

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var usedColors = GetUsedLayerColors(db, tr);

                // Слой на каждый типоразмер сечения (по габаритам bbox), цвета — из
                // палитры далеко разнесённых оттенков, без повторов с уже существующими.
                var sizeLayers = new Dictionary<string, string>();

                int columnCount = 0, skippedOpen = 0;

                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                foreach (SelectedObject so in psr.Value)
                {
                    Polyline pl = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Polyline;
                    if (pl == null) continue;

                    if (!pl.Closed)
                    {
                        skippedOpen++;
                        continue;
                    }

                    string layerName = ColumnLayerNameFor(pl);
                    if (!sizeLayers.ContainsKey(layerName))
                    {
                        EnsureLayer(db, tr, layerName, PickRandomColor(rnd, usedColors));
                        sizeLayers[layerName] = layerName;
                    }

                    pl.Layer = layerName;
                    columnCount++;

                    // Узел в центре сечения пилона — элемент POINT в том же слое
                    Point2d c = PolygonCentroid(GetPolylineVertices(pl));
                    DBPoint centerPt = new DBPoint(new Point3d(c.X, c.Y, 0));
                    centerPt.Layer = layerName;
                    ms.AppendEntity(centerPt);
                    tr.AddNewlyCreatedDBObject(centerPt, true);
                }

                ed.WriteMessage($"\nПилоны: {columnCount} (+точки центров), типоразмеров/слоёв: {sizeLayers.Count}, пропущено незамкнутых: {skippedOpen}\n");
                foreach (var ln in sizeLayers.Keys)
                    ed.WriteMessage($"  {ln}\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHCOLUMNSBAR: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // ОСНОВНОЙ режим пилонов: пилон моделируется ОДНОЙ осевой линией-пластиной.
        // Из прямоугольного контура строится единственная ось — вдоль ДЛИННОЙ стороны,
        // с толщиной, равной короткой стороне, — в слое WALLS(H-<толщина> PILON).
        // Дальше это обычная стена: сетка врезает её как пластину КЭ 44 с толщиной из
        // имени слоя; стержень КЭ 10 не создаётся (нет точки в COLUMNS). Узел точно в
        // центре пилона в поперечном направлении обеспечивает уже сама MESHQUADMESH —
        // она принудительно врезает перпендикуляр через середину оси-стены с суффиксом
        // PILON (см. GetPylonCrossConstraints), поэтому вторую линию рисовать не нужно.
        // Суффикс PILON отличает пилоны от обычных стен. Исходный контур и старые точки
        // центров (COLUMNS) удаляются. Габариты — по bbox: контур должен быть
        // прямоугольником без поворота, как и в MESHCOLUMNSBAR.
        [CommandMethod("MESHCOLUMNCROSS")]
        public void CreateColumnCrossCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHCOLUMNCROSS");
            Database db = doc.Database;

            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите пилоны (замкнутые полилинии): ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            var rnd = new Random();

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var usedColors = GetUsedLayerColors(db, tr);
                var sizeLayers = new Dictionary<string, string>();

                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                // Точки центров от старых запусков MESHCOLUMNSBAR: точка в COLUMNS —
                // триггер стержня КЭ 10 при экспорте, у крестового пилона её быть
                // не должно. Собираются заранее, удаляются попавшие в габарит пилона.
                var oldPts = new List<KeyValuePair<ObjectId, Point2d>>();
                // Оси от прежних запусков: контур пилона больше не стирается, поэтому
                // повторный запуск на том же контуре нарисовал бы вторую ось поверх
                // первой. Ось, чья середина попала в габарит пилона, заменяется новой.
                var oldAxes = new List<KeyValuePair<ObjectId, Point2d>>();
                foreach (ObjectId id in ms)
                {
                    Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e is DBPoint dp && IsColumnLayer(e.Layer))
                        oldPts.Add(new KeyValuePair<ObjectId, Point2d>(id, new Point2d(dp.Position.X, dp.Position.Y)));
                    else if (e is Line oldLn && IsPylonLayer(e.Layer))
                        oldAxes.Add(new KeyValuePair<ObjectId, Point2d>(id, new Point2d(
                            (oldLn.StartPoint.X + oldLn.EndPoint.X) / 2.0,
                            (oldLn.StartPoint.Y + oldLn.EndPoint.Y) / 2.0)));
                }

                int crossCount = 0, skippedOpen = 0, skippedNotRect = 0, erasedPts = 0, erasedAxes = 0;

                foreach (SelectedObject so in psr.Value)
                {
                    Polyline pl = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Polyline;
                    if (pl == null) continue;

                    if (!pl.Closed)
                    {
                        skippedOpen++;
                        continue;
                    }

                    var verts = GetPolylineVertices(pl);
                    double minX = double.MaxValue, minY = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue;
                    foreach (var p in verts)
                    {
                        if (p.X < minX) minX = p.X;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.Y > maxY) maxY = p.Y;
                    }
                    double b = maxX - minX, h = maxY - minY;
                    if (b < 1.0 || h < 1.0) { skippedNotRect++; continue; }

                    // Повёрнутый/непрямоугольный контур bbox описывает неверно:
                    // площадь контура должна совпадать с площадью bbox
                    if (Math.Abs(Math.Abs(PolygonArea(verts)) - b * h) > 0.05 * b * h)
                    {
                        skippedNotRect++;
                        continue;
                    }

                    double cx = (minX + maxX) / 2.0, cy = (minY + maxY) / 2.0;

                    // Единственная ось-пластина — вдоль ДЛИННОЙ стороны пилона, толщина
                    // равна короткой стороне. Поперечный узел в центре обеспечит сама
                    // MESHQUADMESH (перпендикуляр через середину этой оси), поэтому вторую
                    // линию не рисуем.
                    bool xIsLong = b >= h;
                    string wallLayer = xIsLong
                        ? WallLayerPrefix + $"{h:0.###} {PylonMarker})"   // ось вдоль X (длинная), толщина H
                        : WallLayerPrefix + $"{b:0.###} {PylonMarker})";  // ось вдоль Y (длинная), толщина B
                    if (!sizeLayers.ContainsKey(wallLayer))
                    {
                        EnsureLayer(db, tr, wallLayer, PickRandomColor(rnd, usedColors));
                        sizeLayers[wallLayer] = wallLayer;
                    }

                    // Прежняя ось этого же пилона (повторный запуск команды) убирается,
                    // иначе на одном контуре окажутся две совпадающие оси-пластины.
                    foreach (var kv in oldAxes)
                    {
                        if (kv.Value.X < minX - 1.0 || kv.Value.X > maxX + 1.0
                            || kv.Value.Y < minY - 1.0 || kv.Value.Y > maxY + 1.0) continue;
                        Entity e = (Entity)tr.GetObject(kv.Key, OpenMode.ForWrite);
                        if (!e.IsErased) { e.Erase(); erasedAxes++; }
                    }

                    Line lx = xIsLong
                        ? new Line(new Point3d(cx - b / 2.0, cy, 0), new Point3d(cx + b / 2.0, cy, 0))
                        : new Line(new Point3d(cx, cy - h / 2.0, 0), new Point3d(cx, cy + h / 2.0, 0));
                    lx.Layer = wallLayer;
                    ms.AppendEntity(lx);
                    tr.AddNewlyCreatedDBObject(lx, true);

                    // Контур НЕ стирается: MESHQUADMESH отпечатывает его на сетке плиты
                    // (узлы в углах, мелкая сетка внутри). Слой служебный — MESHCLEAN его
                    // сохраняет, MESHLAYERS не уводит, MESHWALLAXIS не считает стеной.
                    EnsureLayer(db, tr, PylonOutlineLayerName, 8); // тёмно-серый
                    pl.Layer = PylonOutlineLayerName;
                    crossCount++;

                    foreach (var kv in oldPts)
                    {
                        if (kv.Value.X < minX - 1.0 || kv.Value.X > maxX + 1.0
                            || kv.Value.Y < minY - 1.0 || kv.Value.Y > maxY + 1.0) continue;
                        Entity e = (Entity)tr.GetObject(kv.Key, OpenMode.ForWrite);
                        if (!e.IsErased) { e.Erase(); erasedPts++; }
                    }
                }

                ed.WriteMessage($"\nПилонов заменено осями-пластинами: {crossCount}, слоёв: {sizeLayers.Count}, пропущено незамкнутых: {skippedOpen}, не прямоугольных/повёрнутых: {skippedNotRect}" +
                    (erasedPts > 0 ? $", удалено старых точек центров: {erasedPts}" : "") +
                    (erasedAxes > 0 ? $", заменено прежних осей: {erasedAxes}" : "") + "\n");
                foreach (var ln in sizeLayers.Keys)
                    ed.WriteMessage($"  {ln}\n");
                if (crossCount > 0)
                    ed.WriteMessage($"Контуры пилонов сохранены в слое {PylonOutlineLayerName}: MESHQUADMESH отпечатает их на сетке плиты (узлы в углах, внутри сетка {MeshTol.PylonInnerCell:0} мм). Ось пилона ведёт себя как стена: врежется в сетку, получит узел в центре поперёк оси, экспорт даст пластины КЭ 44.\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHCOLUMNCROSS: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // Контур должен быть пригоден для построения сетки и импорта в ЛИРУ:
        // замкнут, без дуг (ЛИРА их не принимает), без самопересечений, с ненулевой
        // площадью. Иначе сетка молча строится кривой — лучше отказать сразу.
        private bool ValidateContour(Polyline pline, Editor ed, Transaction tr, Database db, out List<Point2d> pts)
        {
            pts = null;

            // Старые маркеры разрывов стираются при каждой проверке, чтобы не копились
            EraseMarksOnLayer(tr, db, GapMarkLayerName);

            if (!pline.Closed)
            {
                // Разрыв открытой полилинии — между её конечной и начальной вершинами:
                // оба конца отмечаются красными кругами Ø300 мм.
                var gapVerts = GetPolylineVertices(pline);
                var gapPts = new List<Point2d> { gapVerts[0] };
                Point2d last = gapVerts[gapVerts.Count - 1];
                if (last.GetDistanceTo(gapPts[0]) > 1e-6) gapPts.Add(last);
                DrawMarkCircles(tr, db, GapMarkLayerName, gapPts, GapMarkRadius);
                ed.WriteMessage($"\nОшибка: полилиния не замкнута. Нужен замкнутый контур. Места разрыва отмечены красными кругами Ø300 в слое {GapMarkLayerName}.\n");
                return false;
            }

            int nv = pline.NumberOfVertices;
            for (int i = 0; i < nv; i++)
            {
                if (pline.GetSegmentType(i) == SegmentType.Arc)
                {
                    ed.WriteMessage($"\nОшибка: контур содержит дугу (за вершиной {i}). Дуги не поддерживаются и не допускаются при импорте в ЛИРА-САПР — замените дуги хордами.\n");
                    return false;
                }
            }

            // Контур вне плоскости XY: вершины берутся в WCS и геометрия остаётся
            // верной, но отметка Z теряется — сетка ляжет на нулевую отметку.
            if (!IsPolylineFlatXY(pline))
                ed.WriteMessage("\nВНИМАНИЕ: контур плиты лежит не в плоскости XY (нормаль не +Z или ненулевая отметка). Сетка строится по проекции на XY.\n");

            pts = CleanupPolygon(GetPolylineVertices(pline));
            if (pts.Count < 3 || Math.Abs(PolygonArea(pts)) < 1e-6)
            {
                ed.WriteMessage("\nОшибка: контур вырожден (меньше 3 несовпадающих вершин или нулевая площадь).\n");
                return false;
            }

            // Самопересечения (общий поиск с MESHCHECK, Geometry.cs): здесь построение
            // останавливается на первом — контур всё равно нужно править.
            var selfInts = FindSelfIntersections(pts);
            if (selfInts.Count > 0)
            {
                DrawMarkCircles(tr, db, ProblemLayerName, new List<Point2d> { selfInts[0] }, ProblemMarkRadius);
                ed.WriteMessage($"\nОшибка: контур самопересекается (пересечений сторон: {selfInts.Count}). Первое место ({selfInts[0].X:0}, {selfInts[0].Y:0}) отмечено кругом в слое {ProblemLayerName}. Исправьте контур.\n");
                return false;
            }

            // Углы, не равные 90°: не ошибка (бывает кривая подоснова), но сетка у
            // таких углов будет хуже — предупреждение с координатами и круг-маркер.
            List<double> cornerAngles;
            var badIdx = FindNonRightCorners(pts, out cornerAngles);
            var badPts = new List<Point2d>();
            foreach (int i in badIdx) badPts.Add(pts[i]);
            if (badIdx.Count > 0)
            {
                ed.WriteMessage($"\nВНИМАНИЕ: углы контура отличаются от 90° ({badIdx.Count} шт.):\n");
                int show = Math.Min(badIdx.Count, 10);
                for (int i = 0; i < show; i++)
                    ed.WriteMessage($"  вершина {badIdx[i]} ({pts[badIdx[i]].X:0}, {pts[badIdx[i]].Y:0}) — {cornerAngles[i]:0.0}°\n");
                if (badIdx.Count > show)
                    ed.WriteMessage($"  ... и ещё {badIdx.Count - show}\n");
            }

            // Маркеры кривых углов: старые стираются (чтобы не копились от прошлых
            // проверок), на каждый кривой угол ставится круг толщиной линии 0.35 мм
            // в красном слое MESH_ANGLE_MARKS.
            EraseMarksOnLayer(tr, db, AngleMarkLayerName);
            if (badPts.Count > 0)
            {
                DrawMarkCircles(tr, db, AngleMarkLayerName, badPts, AngleMarkRadius);
                ed.WriteMessage($"Кривые углы отмечены кругами в слое {AngleMarkLayerName}.\n");
            }

            return true;
        }

        // Имена слоёв и радиусы маркеров — в Defs.cs (реестр слоёв плагина).

        // Маркировка проблем отдельной транзакцией — для случаев, когда основная
        // транзакция команды откатывается (чертёж не меняется, круги остаются).
        // Вызывать только после tr.Abort()/Dispose основной транзакции.
        private void MarkProblemPoints(Database db, List<Point2d> pts)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EraseMarksOnLayer(tr, db, ProblemLayerName);
                DrawMarkCircles(tr, db, ProblemLayerName, pts, ProblemMarkRadius);
                tr.Commit();
            }
        }

        private void EraseMarksOnLayer(Transaction tr, Database db, string layerName)
        {
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            var oldMarks = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (e != null && e.Layer == layerName) oldMarks.Add(id);
            }
            foreach (ObjectId id in oldMarks)
            {
                Entity e = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                e.Erase();
            }
        }

        // Середина дверного отрезка и его единичное направление — заготовка для
        // квадрата-обозначения. Вырожденный отрезок (точка) пропускается.
        private void CollectDoorMark(Point2d a, Point2d b, List<Point2d> mids, List<Point2d> axes)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return;

            mids.Add(new Point2d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0));
            axes.Add(new Point2d(dx / len, dy / len));
        }

        // Квадраты-обозначения дверных проёмов: сторона DoorMarkSize, центр — середина
        // дверного отрезка, стороны развёрнуты вдоль стены (для наклонных стен тоже).
        // Старое обозначение того же проёма стирается, чтобы повторный запуск
        // MESHDOORS на тех же отрезках не накапливал квадраты друг на друге.
        private int DrawDoorMarks(Transaction tr, Database db, List<Point2d> mids, List<Point2d> axes)
        {
            if (mids.Count == 0) return 0;

            EnsureLayer(db, tr, DoorMarkLayerName, 30);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

            var stale = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (e == null || e.Layer != DoorMarkLayerName) continue;

                Polyline old = e as Polyline;
                if (old == null || old.NumberOfVertices == 0) continue;

                var ov = GetPolylineVertices(old);
                double cx = 0, cy = 0;
                foreach (var v in ov) { cx += v.X; cy += v.Y; }
                var center = new Point2d(cx / ov.Count, cy / ov.Count);

                foreach (var m in mids)
                    if (center.GetDistanceTo(m) < DoorMarkSize) { stale.Add(id); break; }
            }
            foreach (ObjectId id in stale)
                ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Erase();

            double h = DoorMarkSize / 2.0;
            int drawn = 0;
            for (int i = 0; i < mids.Count; i++)
            {
                Point2d c = mids[i], u = axes[i];
                // v — нормаль к оси двери; углы квадрата: c ± u*h ± v*h.
                Point2d v = new Point2d(-u.Y, u.X);
                var corners = new Point2d[]
                {
                    new Point2d(c.X - u.X * h - v.X * h, c.Y - u.Y * h - v.Y * h),
                    new Point2d(c.X + u.X * h - v.X * h, c.Y + u.Y * h - v.Y * h),
                    new Point2d(c.X + u.X * h + v.X * h, c.Y + u.Y * h + v.Y * h),
                    new Point2d(c.X - u.X * h + v.X * h, c.Y - u.Y * h + v.Y * h),
                };

                Polyline sq = new Polyline();
                for (int k = 0; k < 4; k++)
                    sq.AddVertexAt(k, corners[k], 0, 0, 0);
                sq.Closed = true;
                sq.Layer = DoorMarkLayerName;
                ms.AppendEntity(sq);
                tr.AddNewlyCreatedDBObject(sq, true);
                drawn++;
            }
            return drawn;
        }

        private void DrawMarkCircles(Transaction tr, Database db, string layerName, List<Point2d> pts, double radius)
        {
            EnsureLayer(db, tr, layerName, 1); // красный
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            foreach (var p in pts)
            {
                Circle c = new Circle(new Point3d(p.X, p.Y, 0), Vector3d.ZAxis, radius);
                c.Layer = layerName;
                c.LineWeight = LineWeight.LineWeight035;
                ms.AppendEntity(c);
                tr.AddNewlyCreatedDBObject(c, true);
            }
        }

        // Имена слоёв и проверки IsColumnLayer/IsServiceLayer — в Defs.cs.

        private string ColumnLayerNameFor(Polyline pl)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in GetPolylineVertices(pl))
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            return $"COLUMNS(SEC-RC_RECT B-{maxX - minX:0.###} H-{maxY - minY:0.###})";
        }

        // Разбивает замкнутые полилинии-контуры пилонов (слой COLUMNS) на отдельные
        // отрезки в слое линий триангуляции; исходная полилиния удаляется.
        private int ExplodeColumnContours(Transaction tr, Database db)
        {
            var plineIds = new List<ObjectId>();
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || !IsColumnLayer(ent.Layer)) continue;
                Polyline pl = ent as Polyline;
                if (pl == null || !pl.Closed) continue;
                plineIds.Add(id);
            }

            if (plineIds.Count == 0) return 0;

            var rnd = new Random();
            var usedColors = GetUsedLayerColors(db, tr);
            EnsureLayer(db, tr, TriangulationLayerName, PickRandomColor(rnd, usedColors));

            foreach (ObjectId id in plineIds)
            {
                Polyline pl = (Polyline)tr.GetObject(id, OpenMode.ForWrite);
                var verts = GetPolylineVertices(pl);
                int n = verts.Count;

                for (int i = 0; i < n; i++)
                {
                    Point2d a = verts[i];
                    Point2d b = verts[(i + 1) % n];
                    if (a.GetDistanceTo(b) < 1e-9) continue;

                    Line line = new Line(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0));
                    line.Layer = TriangulationLayerName;
                    btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);
                }

                pl.Erase();
            }

            return plineIds.Count;
        }


        // Пилон допускается сдвигать целиком (жёсткий перенос, размеры сечения не меняются)
        // к линиям сетки не более чем на WallSnapTolerance мм — для чистоты сетки.
        // Привязка по левому нижнему углу bbox, покоординатно.
        private int SnapColumnsToGrid(Transaction tr, Database db, double minX, double minY, double cellSize)
        {
            int moved = 0;
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            var plineIds = new List<ObjectId>();
            var pointIds = new List<ObjectId>();

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || !IsColumnLayer(ent.Layer)) continue;

                Polyline pl = ent as Polyline;
                if (pl != null && pl.Closed) { plineIds.Add(id); continue; }
                if (ent is DBPoint) pointIds.Add(id);
            }

            foreach (ObjectId id in plineIds)
            {
                Polyline pl = (Polyline)tr.GetObject(id, OpenMode.ForRead);

                var polyPts = GetPolylineVertices(pl);
                double cMinX = double.MaxValue, cMinY = double.MaxValue;
                foreach (var p in polyPts)
                {
                    if (p.X < cMinX) cMinX = p.X;
                    if (p.Y < cMinY) cMinY = p.Y;
                }

                double dx = SnapCoord(cMinX, minX, cellSize) - cMinX;
                double dy = SnapCoord(cMinY, minY, cellSize) - cMinY;

                if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) continue;

                // Точки центров (POINT в слое COLUMNS) внутри контура — до сдвига,
                // чтобы двигались вместе с пилоном
                var disp = Matrix3d.Displacement(new Vector3d(dx, dy, 0));

                foreach (ObjectId pid in pointIds)
                {
                    DBPoint dp = (DBPoint)tr.GetObject(pid, OpenMode.ForRead);
                    Point2d pos = new Point2d(dp.Position.X, dp.Position.Y);
                    if (!IsPointInPolygon(pos, polyPts)) continue;

                    dp.UpgradeOpen();
                    dp.TransformBy(disp);
                }

                pl.UpgradeOpen();
                pl.TransformBy(disp);
                moved++;
            }

            return moved;
        }

        // Собирает замкнутые полилинии-сечения пилонов со слоя COLUMNS.
        private List<List<Point2d>> GetColumnPolygons(Transaction tr, Database db)
        {
            var result = new List<List<Point2d>>();
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || !IsColumnLayer(ent.Layer)) continue;

                Polyline pl = ent as Polyline;
                if (pl == null || !pl.Closed) continue;

                var verts = GetPolylineVertices(pl);
                if (verts.Count < 3) continue;
                EnsureCcw(verts);
                result.Add(verts);
            }

            return result;
        }

        // Собирает замкнутые полилинии-контуры отверстий со слоя MESH_HOLES.
        private List<List<Point2d>> GetHolePolygons(Transaction tr, Database db)
        {
            var result = new List<List<Point2d>>();
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || ent.Layer != HoleLayerName) continue;

                Polyline pl = ent as Polyline;
                if (pl == null || !pl.Closed) continue;

                var verts = GetPolylineVertices(pl);
                if (verts.Count < 3) continue;
                EnsureCcw(verts);
                result.Add(verts);
            }

            return result;
        }

        // Переносит интерактивно выбранные замкнутые полилинии на слой отверстий.
        // Не трогает сам контур плиты (skipId) и служебные слои плагина (стены/пилоны/
        // линии сетки уже несут смысл). Незамкнутые полилинии пропускаются с
        // предупреждением. Возвращает число фактически перенесённых контуров.
        private int MovePolylinesToHoleLayer(Transaction tr, Database db, List<ObjectId> ids, ObjectId skipId)
        {
            if (ids == null || ids.Count == 0) return 0;

            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
            EnsureLayer(db, tr, HoleLayerName, 6); // сиреневый
            int moved = 0;

            foreach (ObjectId id in ids)
            {
                if (id == skipId || id.IsErased) continue;
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                if (ent.Layer == HoleLayerName) continue; // уже отверстие
                if (IsServiceLayer(ent.Layer)) continue;  // не превращаем стену/пилон/сетку в отверстие

                Polyline pl = ent as Polyline;
                if (pl == null) continue;
                if (!pl.Closed)
                {
                    ed.WriteMessage($"\nПропущен незамкнутый контур отверстия (Handle {ent.Handle}) — контур отверстия должен быть замкнут.\n");
                    continue;
                }

                ent.UpgradeOpen();
                ent.Layer = HoleLayerName;
                moved++;
            }

            return moved;
        }


        private static readonly short[] LayerColorPalette = new short[] { 1, 2, 3, 4, 5, 6, 30, 50, 90, 140, 200, 220 };

        // Цвет слоя ни при каких обстоятельствах не должен совпадать с цветом
        // уже существующего слоя (used заполняется из таблицы слоёв чертежа).
        private short PickRandomColor(System.Random rnd, HashSet<short> used)
        {
            var available = new List<short>();
            foreach (var c in LayerColorPalette)
                if (!used.Contains(c)) available.Add(c);

            if (available.Count > 0)
            {
                short color = available[rnd.Next(available.Count)];
                used.Add(color);
                return color;
            }

            // Палитра исчерпана — берём первый свободный ACI-цвет (7 = белый, пропускаем).
            for (short c = 1; c <= 255; c++)
            {
                if (c == 7 || used.Contains(c)) continue;
                used.Add(c);
                return c;
            }
            return 1;
        }

        private HashSet<short> GetUsedLayerColors(Database db, Transaction tr)
        {
            var used = new HashSet<short>();
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                LayerTableRecord ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                used.Add(ltr.Color.ColorIndex);
            }
            return used;
        }

        private void EnsureLayer(Database db, Transaction tr, string layerName, short colorIndex)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layerName)) return;

            lt.UpgradeOpen();
            LayerTableRecord ltr = new LayerTableRecord();
            ltr.Name = layerName;
            ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex);
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }


        private const double WallSnapTolerance = MeshTol.WallSnap;

        private double SnapCoord(double v, double origin, double cellSize)
        {
            double snapped = origin + Math.Round((v - origin) / cellSize) * cellSize;
            return Math.Abs(snapped - v) <= WallSnapTolerance ? snapped : v;
        }

        // Стены допускается сдвигать к линиям сетки не более чем на WallSnapTolerance мм —
        // это убирает узкие полосы между стеной и рядом идущей линией сетки.
        // Двигаются сами объекты на слоях WALLS(H-...), чтобы чертёж совпадал с сеткой.
        private int SnapWallsToGrid(Transaction tr, Database db, double minX, double minY, double cellSize)
        {
            int moved = 0;
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                if (!IsWallLayer(ent.Layer)) continue;
                // Оси пилона-креста не двигаем: сдвиг длинной оси разорвал бы крест
                // (короткая ось в LINE_TRIANGULATION не снапится) и увёл бы центр.
                if (IsPylonLayer(ent.Layer)) continue;

                Line line = ent as Line;
                if (line != null)
                {
                    Point3d s = line.StartPoint;
                    Point3d e = line.EndPoint;
                    Point3d ns = new Point3d(SnapCoord(s.X, minX, cellSize), SnapCoord(s.Y, minY, cellSize), s.Z);
                    Point3d ne = new Point3d(SnapCoord(e.X, minX, cellSize), SnapCoord(e.Y, minY, cellSize), e.Z);

                    if (ns.DistanceTo(s) > 1e-9 || ne.DistanceTo(e) > 1e-9)
                    {
                        line.UpgradeOpen();
                        line.StartPoint = ns;
                        line.EndPoint = ne;
                        moved++;
                    }
                    continue;
                }

                Polyline wallPline = ent as Polyline;
                if (wallPline != null)
                {
                    // Не в плоскости XY — SetPointAt писал бы в OCS и увёл геометрию.
                    if (!IsPolylineFlatXY(wallPline)) continue;

                    bool changed = false;
                    int n = wallPline.NumberOfVertices;
                    for (int i = 0; i < n; i++)
                    {
                        Point2d p = wallPline.GetPoint2dAt(i);
                        Point2d np = new Point2d(SnapCoord(p.X, minX, cellSize), SnapCoord(p.Y, minY, cellSize));
                        if (np.GetDistanceTo(p) > 1e-9)
                        {
                            if (!changed) { wallPline.UpgradeOpen(); changed = true; }
                            wallPline.SetPointAt(i, np);
                        }
                    }
                    if (changed) moved++;
                }
            }

            return moved;
        }


        // Пилон-ось (MESHCOLUMNCROSS): пилон задаётся ОДНОЙ линией вдоль длинной стороны
        // в слое WALLS(H-<t> PILON). Чтобы сетка гарантированно получила узел точно в
        // центре пилона в ПОПЕРЕЧНОМ направлении, MESHQUADMESH принудительно врезает
        // перпендикуляр через середину этой оси. Здесь такие перпендикуляры и строятся:
        // для каждой оси-стены с суффиксом PILON — отрезок через её середину, поперёк
        // оси, длиной в толщину t (короткую сторону пилона) из имени слоя.
        // ВАЖНО: перпендикуляры используются ТОЛЬКО как линии разреза ячеек (создают
        // узлы), но НЕ как «стены» — их нельзя блокировать в ResolveOverlappingSegments,
        // иначе поперечное ребро сетки удалится как совпавшее со стеной, и линия снова
        // окажется «проигнорированной». Поэтому они не входят в wallSegments/cutSegments.
        private List<Point2d[]> GetPylonCrossConstraints(Transaction tr, Database db)
        {
            var result = new List<Point2d[]>();
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Line ln = tr.GetObject(id, OpenMode.ForRead) as Line;
                if (ln == null) continue;
                if (!IsPylonLayer(ln.Layer)) continue;
                double t;
                if (!TryParseLayerHeight(ln.Layer, out t)) continue;

                double dx = ln.EndPoint.X - ln.StartPoint.X, dy = ln.EndPoint.Y - ln.StartPoint.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6) continue;
                double ux = dx / len, uy = dy / len;          // направление оси
                double px = -uy, py = ux;                     // перпендикуляр (единичный)
                double mx = (ln.StartPoint.X + ln.EndPoint.X) / 2.0;
                double my = (ln.StartPoint.Y + ln.EndPoint.Y) / 2.0;
                double half = t / 2.0;

                result.Add(new Point2d[]
                {
                    new Point2d(mx - px * half, my - py * half),
                    new Point2d(mx + px * half, my + py * half)
                });
            }
            return result;
        }


        // Контуры пилонов-пластин для ОТПЕЧАТКА на сетке плиты (слой MESH_PYLONS,
        // сохраняется командой MESHCOLUMNCROSS). Это не пустота, как COLUMNS: внутри
        // отпечатка сетка плиты есть, только мельче. Возвращаются прямоугольники в CCW.
        //
        // Фолбэк по осям нужен для чертежей, сделанных до появления слоя: там
        // MESHCOLUMNCROSS контур ещё стирал, и восстановить прямоугольник можно только
        // из самой оси — её длина даёт длинную сторону, толщина из имени слоя короткую.
        // Ось, уже накрытая сохранённым контуром, второй раз не берётся.
        private List<List<Point2d>> GetPylonOutlines(
            Transaction tr, Database db, out int fromAxes, out int skippedNotRect)
        {
            var result = new List<List<Point2d>>();
            fromAxes = 0;
            skippedNotRect = 0;
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            List<Point2d> Rect(double x0, double y0, double x1, double y1)
            {
                return new List<Point2d>
                {
                    new Point2d(x0, y0), new Point2d(x1, y0),
                    new Point2d(x1, y1), new Point2d(x0, y1)
                };
            }

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || ent.Layer != PylonOutlineLayerName) continue;

                Polyline pl = ent as Polyline;
                if (pl == null || !pl.Closed) continue;

                var verts = GetPolylineVertices(pl);
                if (verts.Count < 3) continue;

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var p in verts)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }
                double b = maxX - minX, h = maxY - minY;

                // Отпечаток строится линиями сетки, поэтому повёрнутый контур отпечатать
                // нечем: bbox описал бы его неверно. Признак тот же, что в MESHCOLUMNCROSS.
                if (b < 1.0 || h < 1.0
                    || Math.Abs(Math.Abs(PolygonArea(verts)) - b * h) > 0.05 * b * h)
                {
                    skippedNotRect++;
                    continue;
                }

                result.Add(Rect(minX, minY, maxX, maxY));
            }

            int savedCount = result.Count;

            foreach (ObjectId id in btr)
            {
                Line ln = tr.GetObject(id, OpenMode.ForRead) as Line;
                if (ln == null || !IsPylonLayer(ln.Layer)) continue;
                double t;
                if (!TryParseLayerHeight(ln.Layer, out t) || t < 1.0) continue;

                double dx = ln.EndPoint.X - ln.StartPoint.X, dy = ln.EndPoint.Y - ln.StartPoint.Y;
                if (Math.Sqrt(dx * dx + dy * dy) < 1.0) continue;

                Point2d mid = new Point2d(
                    (ln.StartPoint.X + ln.EndPoint.X) / 2.0,
                    (ln.StartPoint.Y + ln.EndPoint.Y) / 2.0);

                bool covered = false;
                for (int i = 0; i < savedCount; i++)
                    if (IsPointInPolygon(mid, result[i])) { covered = true; break; }
                if (covered) continue;

                bool horizontal = Math.Abs(dy) < MeshTol.Collinear;
                bool vertical = Math.Abs(dx) < MeshTol.Collinear;
                if (!horizontal && !vertical) { skippedNotRect++; continue; }

                double half = t / 2.0;
                if (horizontal)
                {
                    double x0 = Math.Min(ln.StartPoint.X, ln.EndPoint.X);
                    double x1 = Math.Max(ln.StartPoint.X, ln.EndPoint.X);
                    result.Add(Rect(x0, mid.Y - half, x1, mid.Y + half));
                }
                else
                {
                    double y0 = Math.Min(ln.StartPoint.Y, ln.EndPoint.Y);
                    double y1 = Math.Max(ln.StartPoint.Y, ln.EndPoint.Y);
                    result.Add(Rect(mid.X - half, y0, mid.X + half, y1));
                }
                fromAxes++;
            }

            return result;
        }


        // Собирает отрезки стен со всех слоёв WALLS(H-...), созданных командой MESHWALLS.
        private List<Point2d[]> GetWallSegments(Transaction tr, Database db)
        {
            var result = new List<Point2d[]>();
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                if (!IsWallLayer(ent.Layer)) continue;

                Line line = ent as Line;
                if (line != null)
                {
                    result.Add(new Point2d[]
                    {
                        new Point2d(line.StartPoint.X, line.StartPoint.Y),
                        new Point2d(line.EndPoint.X, line.EndPoint.Y)
                    });
                    continue;
                }

                Polyline wallPline = ent as Polyline;
                if (wallPline != null)
                {
                    var verts = GetPolylineVertices(wallPline);
                    int n = verts.Count;
                    int segCount = wallPline.Closed ? n : n - 1;
                    for (int i = 0; i < segCount; i++)
                    {
                        result.Add(new Point2d[] { verts[i], verts[(i + 1) % n] });
                    }
                }
            }

            return result;
        }


        // Концы дверных отрезков со слоёв WALL_DOORS(H-...) — «косяки» проёма.
        // MESHQUADMESH ставит узлы сетки в этих точках, чтобы куски стены точно
        // совпали с проёмом (иначе он съезжал к ближайшему узлу сетки).
        private List<Point2d> GetDoorEndpoints(Transaction tr, Database db)
        {
            var result = new List<Point2d>();
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                if (!IsDoorLayer(ent.Layer)) continue;

                if (ent is Line line)
                {
                    result.Add(new Point2d(line.StartPoint.X, line.StartPoint.Y));
                    result.Add(new Point2d(line.EndPoint.X, line.EndPoint.Y));
                }
                else if (ent is Polyline pl)
                {
                    var verts = GetPolylineVertices(pl);
                    int n = verts.Count;
                    int segCount = pl.Closed ? n : n - 1;
                    for (int i = 0; i < segCount; i++)
                    {
                        result.Add(verts[i]);
                        result.Add(verts[(i + 1) % n]);
                    }
                }
            }
            return result;
        }

        // Правило: узлы на оси пилона не ближе MeshTol.MinElementSize друг к другу.
        // Линия сетки, прошедшая в 20–80 мм от центра или конца оси, режет пластину
        // на КЭ в единицы миллиметров.
        //
        // Цели выравнивания сетки по осям пилонов: концы оси и её середина (там узел
        // ставит поперечный разрез креста). Линия сетки, оказавшаяся ближе допуска,
        // садится ровно на эту точку — зазор становится нулевым вместо 20–80 мм, а
        // следующая линия отстоит на полный шаг. Саму ось двигать нельзя
        // (SnapWallsToGrid её пропускает — иначе крест разъезжается), поэтому
        // двигаются линии сетки.
        private void GetPylonAxisTargets(Transaction tr, Database db, List<double> xs, List<double> ys)
        {
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Line ln = tr.GetObject(id, OpenMode.ForRead) as Line;
                if (ln == null || string.IsNullOrEmpty(ln.Layer)) continue;
                if (!IsPylonLayer(ln.Layer)) continue;

                double dx = ln.EndPoint.X - ln.StartPoint.X, dy = ln.EndPoint.Y - ln.StartPoint.Y;
                if (Math.Sqrt(dx * dx + dy * dy) < 1e-6) continue;

                if (Math.Abs(dy) >= Math.Abs(dx))
                {
                    ys.Add(ln.StartPoint.Y);
                    ys.Add(ln.EndPoint.Y);
                    ys.Add((ln.StartPoint.Y + ln.EndPoint.Y) / 2.0);
                    xs.Add(ln.StartPoint.X); // сама ось: линия сетки садится на неё
                }
                else
                {
                    xs.Add(ln.StartPoint.X);
                    xs.Add(ln.EndPoint.X);
                    xs.Add((ln.StartPoint.X + ln.EndPoint.X) / 2.0);
                    ys.Add(ln.StartPoint.Y);
                }
            }
        }

        // Дверной отрезок допускается подвинуть к линии сетки, чтобы косяк сел точно
        // в узел и поперечный разрез не оставлял узкой полосы. Порог меньше, чем у
        // стен (WallSnapTolerance): дверь двигать сильнее нельзя — поедет проём.
        private const double DoorSnapTolerance = MeshTol.DoorSnap;

        // Снап косяков к линиям сетки. Двигается ТОЛЬКО координата вдоль стены:
        // у горизонтальной двери X, у вертикальной Y. Поперечную координату трогать
        // нельзя — дверь сойдёт с оси стены, и экспорт перестанет её узнавать
        // (кусок стены ищется по середине на дверном отрезке с допуском 1 мм).
        // Концы снапятся независимо, поэтому ширина проёма может измениться на
        // величину до двух допусков.
        private int SnapDoorsToGrid(Transaction tr, Database db, List<double> xs, List<double> ys)
        {
            int moved = 0;
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            var ids = new List<ObjectId>();
            foreach (ObjectId id in btr)
            {
                Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (e == null || string.IsNullOrEmpty(e.Layer)) continue;
                if (IsDoorLayer(e.Layer)) ids.Add(id);
            }

            foreach (ObjectId id in ids)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;

                if (ent is Line ln)
                {
                    bool horiz = Math.Abs(ln.EndPoint.X - ln.StartPoint.X) >= Math.Abs(ln.EndPoint.Y - ln.StartPoint.Y);
                    double s = horiz ? SnapToNearest(ln.StartPoint.X, xs) : SnapToNearest(ln.StartPoint.Y, ys);
                    double e2 = horiz ? SnapToNearest(ln.EndPoint.X, xs) : SnapToNearest(ln.EndPoint.Y, ys);

                    bool changed = false;
                    if (horiz)
                    {
                        if (Math.Abs(s - ln.StartPoint.X) > 1e-9) { ln.StartPoint = new Point3d(s, ln.StartPoint.Y, ln.StartPoint.Z); changed = true; }
                        if (Math.Abs(e2 - ln.EndPoint.X) > 1e-9) { ln.EndPoint = new Point3d(e2, ln.EndPoint.Y, ln.EndPoint.Z); changed = true; }
                    }
                    else
                    {
                        if (Math.Abs(s - ln.StartPoint.Y) > 1e-9) { ln.StartPoint = new Point3d(ln.StartPoint.X, s, ln.StartPoint.Z); changed = true; }
                        if (Math.Abs(e2 - ln.EndPoint.Y) > 1e-9) { ln.EndPoint = new Point3d(ln.EndPoint.X, e2, ln.EndPoint.Z); changed = true; }
                    }
                    if (changed) moved++;
                }
                else if (ent is Polyline pl)
                {
                    if (!IsPolylineFlatXY(pl)) continue; // SetPointAt пишет в OCS

                    var verts = GetPolylineVertices(pl);
                    if (verts.Count < 2) continue;

                    // Направление берём по всей полилинии: дверь всегда прямая.
                    bool horiz = Math.Abs(verts[verts.Count - 1].X - verts[0].X) >= Math.Abs(verts[verts.Count - 1].Y - verts[0].Y);
                    bool changed = false;
                    for (int i = 0; i < pl.NumberOfVertices; i++)
                    {
                        Point2d v = pl.GetPoint2dAt(i);
                        Point2d nv = horiz
                            ? new Point2d(SnapToNearest(v.X, xs), v.Y)
                            : new Point2d(v.X, SnapToNearest(v.Y, ys));
                        if (nv.GetDistanceTo(v) > 1e-9) { pl.SetPointAt(i, nv); changed = true; }
                    }
                    if (changed) moved++;
                }
            }
            return moved;
        }

        // Ближайшая координата сетки, если она в пределах DoorSnapTolerance.
        private double SnapToNearest(double v, List<double> coords)
        {
            double best = v, bestD = DoorSnapTolerance;
            foreach (double c in coords)
            {
                double d = Math.Abs(c - v);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        // Косяки дверных проёмов обязаны быть узлами сетки и связываться с плитой.
        // Вдоль оси стены линий сетки нет (их снимает ResolveOverlappingSegments как
        // совпавшие со стеной), поэтому просто «разрезать в точке» нечего. Для каждого
        // конца дверного отрезка строим короткий ПЕРПЕНДИКУЛЯР и кладём его в
        // splitConstraints: ячейки по обе стороны стены режутся линией через косяк, и в
        // нём сходятся рёбра сетки — ровно как поперечная ось в центре пилона.
        // Дополнительно координата косяка возвращается «мягкой» целью для BuildGridCoords:
        // если линия сетки рядом, она садится точно на косяк и разрез не оставляет
        // узкой полосы; если далеко — полоса будет не уже допуска смещения.
        private List<Point2d[]> GetDoorJambConstraints(
            Transaction tr, Database db, double length,
            List<double> jambXs, List<double> jambYs)
        {
            var result = new List<Point2d[]>();
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                if (!IsDoorLayer(ent.Layer)) continue;

                if (ent is Line ln)
                {
                    AddJambConstraints(
                        new Point2d(ln.StartPoint.X, ln.StartPoint.Y),
                        new Point2d(ln.EndPoint.X, ln.EndPoint.Y),
                        length, result, jambXs, jambYs);
                }
                else if (ent is Polyline pl)
                {
                    var verts = GetPolylineVertices(pl);
                    int n = verts.Count;
                    int segCount = pl.Closed ? n : n - 1;
                    for (int i = 0; i < segCount; i++)
                        AddJambConstraints(verts[i], verts[(i + 1) % n], length, result, jambXs, jambYs);
                }
            }
            return result;
        }

        // Перерисовка квадратов-обозначений после снапа дверей: старый квадрат
        // (в пределах DoorMarkSize от новой середины) стирается внутри DrawDoorMarks.
        private int RedrawAllDoorMarks(Transaction tr, Database db)
        {
            var mids = new List<Point2d>();
            var axes = new List<Point2d>();
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || string.IsNullOrEmpty(ent.Layer)) continue;
                if (!IsDoorLayer(ent.Layer)) continue;

                if (ent is Line ln)
                {
                    CollectDoorMark(
                        new Point2d(ln.StartPoint.X, ln.StartPoint.Y),
                        new Point2d(ln.EndPoint.X, ln.EndPoint.Y), mids, axes);
                }
                else if (ent is Polyline pl)
                {
                    var verts = GetPolylineVertices(pl);
                    int n = verts.Count;
                    int segCount = pl.Closed ? n : n - 1;
                    for (int i = 0; i < segCount; i++)
                        CollectDoorMark(verts[i], verts[(i + 1) % n], mids, axes);
                }
            }
            return DrawDoorMarks(tr, db, mids, axes);
        }

        private void AddJambConstraints(
            Point2d a, Point2d b, double length,
            List<Point2d[]> result, List<double> jambXs, List<double> jambYs)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return;

            double ux = dx / len, uy = dy / len;   // вдоль двери (= вдоль стены)
            double px = -uy, py = ux;              // поперёк
            double half = length / 2.0;

            foreach (var j in new Point2d[] { a, b })
            {
                result.Add(new Point2d[]
                {
                    new Point2d(j.X - px * half, j.Y - py * half),
                    new Point2d(j.X + px * half, j.Y + py * half)
                });

                // Линия сетки нужна поперёк двери: у вертикальной двери это
                // горизонтальная линия (цель по Y), у горизонтальной — вертикальная.
                if (Math.Abs(uy) > Math.Abs(ux)) jambYs.Add(j.Y);
                else jambXs.Add(j.X);
            }
        }

        // Вершины полилинии в МИРОВЫХ координатах.
        //
        // GetPoint2dAt возвращает точку в собственной плоскости полилинии (OCS), а не
        // в WCS: у полилинии с нормалью -Z (обычное дело после зеркалирования в
        // реальных чертежах и в DXF от смежников) координата X приходит с обратным
        // знаком. Плагин считает всю геометрию в WCS, поэтому вершины берутся через
        // GetPoint3dAt, который переводит их в WCS сам.
        //
        // Дуговые сегменты (bulge) здесь превращаются в хорды — об этом
        // предупреждает WarnBadPolylines, вызываемая в начале команд построения.
        private List<Point2d> GetPolylineVertices(Polyline pline)
        {
            var result = new List<Point2d>();
            int n = pline.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                Point3d p = pline.GetPoint3dAt(i);
                result.Add(new Point2d(p.X, p.Y));
            }
            return result;
        }

        // Полилиния лежит в плоскости XY чертежа (нормаль +Z, нулевая отметка).
        // Для не лежащей в XY полилинии запись вершин через SetPointAt (снап стен,
        // пилонов, дверей) исказила бы геометрию: SetPointAt принимает OCS, а
        // считаем мы в WCS. Такие полилинии команды снапа пропускают.
        private bool IsPolylineFlatXY(Polyline pline)
        {
            return pline.Normal.IsParallelTo(Vector3d.ZAxis)
                && pline.Normal.Z > 0
                && Math.Abs(pline.Elevation) < MeshTol.OnSegment;
        }

        // Есть ли в полилинии дуговые сегменты.
        private bool PolylineHasArcs(Polyline pline)
        {
            int n = pline.NumberOfVertices;
            for (int i = 0; i < n; i++)
                if (Math.Abs(pline.GetBulgeAt(i)) > MeshTol.Zero)
                {
                    // У незамкнутой полилинии выпуклость последней вершины не
                    // образует сегмента.
                    if (i < n - 1 || pline.Closed) return true;
                }
            return false;
        }

        // Проверка исходной геометрии перед построением: полилинии рабочих слоёв
        // (стены, пилоны, отверстия, двери, контур плиты) читаются как ломаные по
        // вершинам в WCS. Дуга при этом молча превращается в хорду, а полилиния вне
        // плоскости XY даёт смещённую или зеркальную геометрию. Оба случая раньше
        // проходили незаметно и всплывали уже в ЛИРЕ, поэтому теперь о них
        // сообщается с координатами.
        private void WarnBadPolylines(Transaction tr, Database db, Editor ed)
        {
            var arcPts = new List<string>();
            var skewPts = new List<string>();

            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                Polyline pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                if (pl == null || pl.NumberOfVertices == 0) continue;
                if (!IsServiceLayer(pl.Layer)) continue;
                if (pl.Layer == DoorMarkLayerName) continue; // обозначение, не геометрия

                Point3d p0 = pl.GetPoint3dAt(0);
                string where = $"({p0.X:0}, {p0.Y:0}) [{pl.Layer}]";

                if (PolylineHasArcs(pl)) arcPts.Add(where);
                if (!IsPolylineFlatXY(pl)) skewPts.Add(where);
            }

            void Report(List<string> list, string what)
            {
                if (list.Count == 0) return;
                int show = Math.Min(list.Count, 5);
                string tail = list.Count > show ? $" и ещё {list.Count - show}" : "";
                ed.WriteMessage($"\nВНИМАНИЕ: {what} ({list.Count} шт.): {string.Join(", ", list.GetRange(0, show))}{tail}\n");
            }

            Report(arcPts, "полилинии с дугами — дуга читается как хорда (замените дуги ломаной)");
            Report(skewPts, "полилинии вне плоскости XY (нормаль не +Z или ненулевая отметка) — геометрия может быть смещена или зеркальна; снап их не двигает");
        }


    }
}
