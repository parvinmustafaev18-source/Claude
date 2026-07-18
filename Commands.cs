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
            ed.WriteMessage("\nПривет! Плагин загружен и работает.\n");
        }


        [CommandMethod("MESHLAYERS")]
        public void CreateLayersCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
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

            string slabLayerName = $"FOUNDATION_SLABS(H-{slabThickness:0.###})";
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

                foreach (SelectedObject so in psr.Value)
                {
                    Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;

                    if (!string.IsNullOrEmpty(ent.Layer) && (ent.Layer.StartsWith("WALLS(H-") || IsColumnLayer(ent.Layer)))
                    {
                        skippedWalls++;
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
                    if (!string.IsNullOrEmpty(ent.Layer) && (ent.Layer.StartsWith("WALLS(H-") || IsColumnLayer(ent.Layer))) continue;

                    string expected = null;
                    if (ent is Polyline) expected = slabLayerName;
                    else if (ent is Line) expected = beamLayerName;

                    if (expected != null && ent.Layer != expected)
                    {
                        ent.Layer = expected;
                        fixedCount++;
                    }
                }

                ed.WriteMessage($"\nПлита ({slabLayerName}): {slabCount}, триангуляция ({beamLayerName}): {beamCount}, пропущено стен: {skippedWalls}, исправлено проверкой: {fixedCount}\n");

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

            string wallLayerName = $"WALLS(H-{wallThickness:0.###})";

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
                    string layerName = $"WALLS(H-{t:0.###})";
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

        [CommandMethod("MESHCOLUMNS")]
        public void CreateColumnsLayerCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
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
                    double cx = 0, cy = 0;
                    int n = pl.NumberOfVertices;
                    for (int i = 0; i < n; i++)
                    {
                        Point2d p = pl.GetPoint2dAt(i);
                        cx += p.X;
                        cy += p.Y;
                    }
                    DBPoint centerPt = new DBPoint(new Point3d(cx / n, cy / n, 0));
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
                ed.WriteMessage($"\nОшибка MESHCOLUMNS: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // Контур должен быть пригоден для построения сетки и импорта в ЛИРУ:
        // замкнут, без дуг (ЛИРА их не принимает), без самопересечений, с ненулевой
        // площадью. Иначе сетка молча строится кривой — лучше отказать сразу.
        private bool ValidateContour(Polyline pline, Editor ed, Transaction tr, Database db, out List<Point2d> pts)
        {
            pts = null;

            if (!pline.Closed)
            {
                ed.WriteMessage("\nОшибка: полилиния не замкнута. Нужен замкнутый контур.\n");
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

            pts = CleanupPolygon(GetPolylineVertices(pline));
            if (pts.Count < 3 || Math.Abs(PolygonArea(pts)) < 1e-6)
            {
                ed.WriteMessage("\nОшибка: контур вырожден (меньше 3 несовпадающих вершин или нулевая площадь).\n");
                return false;
            }

            int m = pts.Count;
            for (int i = 0; i < m; i++)
            {
                for (int j = i + 1; j < m; j++)
                {
                    if (j == i + 1 || (i == 0 && j == m - 1)) continue; // смежные стороны

                    if (SegmentsIntersect(pts[i], pts[(i + 1) % m], pts[j], pts[(j + 1) % m]))
                    {
                        ed.WriteMessage($"\nОшибка: контур самопересекается (стороны {i}-{i + 1} и {j}-{(j + 1) % m}). Исправьте контур.\n");
                        return false;
                    }
                }
            }

            // Углы между смежными сторонами: у прямоугольной плиты в каждой вершине
            // стороны перпендикулярны (90°). Отклонение обычно означает кривую
            // подоснову — не ошибка, но сетка у таких углов будет хуже, поэтому
            // предупреждение с координатами и круг-маркер на чертеже. Вершины на
            // прямых участках (угол ≈180°, промежуточная точка на стороне) не углы.
            var badCorners = new List<string>();
            var badPts = new List<Point2d>();
            for (int i = 0; i < m; i++)
            {
                Point2d prev = pts[(i - 1 + m) % m];
                Point2d cur = pts[i];
                Point2d next = pts[(i + 1) % m];
                double l1 = prev.GetDistanceTo(cur), l2 = cur.GetDistanceTo(next);
                if (l1 < 1e-9 || l2 < 1e-9) continue;

                double dot = ((cur.X - prev.X) * (next.X - cur.X) + (cur.Y - prev.Y) * (next.Y - cur.Y)) / (l1 * l2);
                dot = Math.Max(-1.0, Math.Min(1.0, dot));
                double angle = 180.0 - Math.Acos(dot) * 180.0 / Math.PI;

                if (angle > 175.0) continue; // промежуточная точка на прямой стороне
                if (Math.Abs(angle - 90.0) > 0.5)
                {
                    badCorners.Add($"вершина {i} ({cur.X:0}, {cur.Y:0}) — {angle:0.0}°");
                    badPts.Add(cur);
                }
            }
            if (badCorners.Count > 0)
            {
                ed.WriteMessage($"\nВНИМАНИЕ: углы контура отличаются от 90° ({badCorners.Count} шт.):\n");
                int show = Math.Min(badCorners.Count, 10);
                for (int i = 0; i < show; i++)
                    ed.WriteMessage($"  {badCorners[i]}\n");
                if (badCorners.Count > show)
                    ed.WriteMessage($"  ... и ещё {badCorners.Count - show}\n");
            }

            // Маркеры кривых углов: старые стираются (чтобы не копились от прошлых
            // проверок), на каждый кривой угол ставится круг толщиной линии 0.35 мм
            // в красном слое MESH_ANGLE_MARKS.
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            var oldMarks = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                Entity e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (e != null && e.Layer == AngleMarkLayerName) oldMarks.Add(id);
            }
            foreach (ObjectId id in oldMarks)
            {
                Entity e = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                e.Erase();
            }
            if (badPts.Count > 0)
            {
                EnsureLayer(db, tr, AngleMarkLayerName, 1); // красный
                ms.UpgradeOpen();
                foreach (var p in badPts)
                {
                    Circle c = new Circle(new Point3d(p.X, p.Y, 0), Vector3d.ZAxis, AngleMarkRadius);
                    c.Layer = AngleMarkLayerName;
                    c.LineWeight = LineWeight.LineWeight035;
                    ms.AppendEntity(c);
                    tr.AddNewlyCreatedDBObject(c, true);
                }
                ed.WriteMessage($"Кривые углы отмечены кругами в слое {AngleMarkLayerName}.\n");
            }

            return true;
        }

        private const string AngleMarkLayerName = "MESH_ANGLE_MARKS";
        private const double AngleMarkRadius = 300.0;

        private const string ColumnLayerName = "COLUMNS";
        private const string TriangulationLayerName = "LINE_TRIANGULATION";

        // Пилоны лежат в слоях вида COLUMNS(SEC-RC_RECT B-600 H-300) — по одному слою
        // на типоразмер сечения; старый общий слой COLUMNS тоже распознаётся.
        private bool IsColumnLayer(string layer)
        {
            return !string.IsNullOrEmpty(layer) && layer.StartsWith(ColumnLayerName);
        }

        // Служебные слои плагина: объекты, созданные его же командами, не являются
        // исходными контурами для новых построений.
        private bool IsServiceLayer(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return false;
            return layer.StartsWith("FOUNDATION_SLABS(")
                || layer.StartsWith("WALLS(H-")
                || layer == TriangulationLayerName
                || IsColumnLayer(layer);
        }

        private string ColumnLayerNameFor(Polyline pl)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                Point2d p = pl.GetPoint2dAt(i);
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

                double cMinX = double.MaxValue, cMinY = double.MaxValue;
                int n = pl.NumberOfVertices;
                for (int i = 0; i < n; i++)
                {
                    Point2d p = pl.GetPoint2dAt(i);
                    if (p.X < cMinX) cMinX = p.X;
                    if (p.Y < cMinY) cMinY = p.Y;
                }

                double dx = SnapCoord(cMinX, minX, cellSize) - cMinX;
                double dy = SnapCoord(cMinY, minY, cellSize) - cMinY;

                if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) continue;

                // Точки центров (POINT в слое COLUMNS) внутри контура — до сдвига,
                // чтобы двигались вместе с пилоном
                var polyPts = GetPolylineVertices(pl);
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


        private const double WallSnapTolerance = 100.0;

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
                if (string.IsNullOrEmpty(ent.Layer) || !ent.Layer.StartsWith("WALLS(H-")) continue;

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


        // Собирает отрезки стен со всех слоёв WALLS(H-...), созданных командой MESHWALLS.
        private List<Point2d[]> GetWallSegments(Transaction tr, Database db)
        {
            var result = new List<Point2d[]>();
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                if (string.IsNullOrEmpty(ent.Layer) || !ent.Layer.StartsWith("WALLS(H-")) continue;

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


        private List<Point2d> GetPolylineVertices(Polyline pline)
        {
            var result = new List<Point2d>();
            int n = pline.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                result.Add(pline.GetPoint2dAt(i));
            }
            return result;
        }


    }
}
