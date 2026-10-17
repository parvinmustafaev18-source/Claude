using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MeshPlugin
{
    // ЛИНЕЙНАЯ НАГРУЗКА (вес перегородок, парапетов, лестничных маршей).
    //
    // Отрезок рисуется ПО УЗЛАМ СЕТКИ, и экспорт ставит вдоль него стержни КЭ 10
    // с околонулевой жёсткостью — «носители» нагрузки. Нагрузка кладётся на эти
    // стержни штатным видом ЛИРЫ (тот же код 16, что и у пластин), а в плиту
    // уходит через общие узлы. Так она остаётся в ЛИРЕ НАСТОЯЩЕЙ линейной
    // нагрузкой: видна, выделяется, правится, корректно идёт в РСУ — чего не даёт
    // пересчёт в узловые силы.
    //
    // Жёсткость носителя взята с эталона пользователя (ПАРКИНГ_-11ЭТ): S0 с
    // E = 1, сечением 1x1 см и ρ = 1e-008. Ровный ноль не ставим намеренно —
    // на нулевой жёсткости решатель спотыкается, а 1e-008 схему не меняет.
    internal sealed class LineLoadSeg
    {
        public Point2d A, B;
        public double LoadTm;   // интенсивность, т/м (единицы задачи)
    }

    public partial class Commands
    {
        [CommandMethod("LIRLINE")]
        public void LineLoadCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;
            EchoCommandStart(ed, "LIRLINE");
            try
            {
                var options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nВыберите отрезки линейной нагрузки (по узлам сетки): ",
                    AllowDuplicates = false
                };
                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Operator, "<OR"),
                    new TypedValue((int)DxfCode.Start, "LINE"),
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                    new TypedValue((int)DxfCode.Operator, "OR>")
                });
                var selection = ed.GetSelection(options, filter);
                if (selection.Status != PromptStatus.OK) return;

                // Единицы задачи: силы в тоннах, длины в метрах (документ 0,
                // "33;M 1 CM 100 T 1 C 1 /"), поэтому спрашиваем т/м — ровно то,
                // что уйдёт в файл и покажется в ЛИРЕ.
                var prompt = new PromptDoubleOptions("\nЛинейная нагрузка, т/м: ")
                {
                    DefaultValue = 1.0, AllowNegative = false, AllowZero = false
                };
                var answer = ed.GetDouble(prompt);
                if (answer.Status != PromptStatus.OK) return;
                double load = Math.Round(answer.Value, 4);
                if (!SlabZoneCore.Finite(load) || load <= 0)
                {
                    ed.WriteMessage("\nНужна положительная конечная нагрузка с точностью до 0.0001 т/м.\n");
                    return;
                }

                string layerName = LineLoadLayerPrefix
                    + load.ToString("0.####", CultureInfo.InvariantCulture) + ")";

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var taken = new List<Entity>();
                    foreach (ObjectId id in selection.Value.GetObjectIds())
                    {
                        var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (entity == null || entity.OwnerId != db.CurrentSpaceId)
                            throw new InvalidOperationException("Выбирайте отрезки в текущем пространстве чертежа.");
                        if (IsServiceLayer(entity.Layer) && !IsLineLoadLayer(entity.Layer))
                            throw new InvalidOperationException($"Отрезок {id.Handle} на служебном слое {entity.Layer}. Начертите отдельную линию нагрузки.");
                        var sourceLayer = (LayerTableRecord)tr.GetObject(entity.LayerId, OpenMode.ForRead);
                        if (sourceLayer.IsLocked)
                            throw new InvalidOperationException($"Слой {entity.Layer} заблокирован.");

                        if (entity is Line line)
                        {
                            if (Math.Abs(line.StartPoint.Z) > MeshTol.OnSegment || Math.Abs(line.EndPoint.Z) > MeshTol.OnSegment)
                                throw new InvalidOperationException($"Отрезок {id.Handle} не на нулевой отметке.");
                            if (line.StartPoint.DistanceTo(line.EndPoint) <= MeshTol.NodeMerge)
                                throw new InvalidOperationException($"Отрезок {id.Handle} вырожденный.");
                        }
                        else if (entity is Polyline pline)
                        {
                            if (!IsPolylineFlatXY(pline))
                                throw new InvalidOperationException($"Полилиния {id.Handle} лежит не в плоскости XY на нулевой отметке.");
                            if (PolylineHasArcs(pline))
                                throw new InvalidOperationException($"В полилинии {id.Handle} есть дуги: замените дугу ломаной.");
                            if (pline.NumberOfVertices < 2)
                                throw new InvalidOperationException($"В полилинии {id.Handle} меньше двух вершин.");
                        }
                        else continue;

                        taken.Add(entity);
                    }

                    if (taken.Count == 0)
                    {
                        ed.WriteMessage("\nНи одного пригодного отрезка — ничего не изменено.\n");
                        return;
                    }

                    EnsureLayer(db, tr, layerName, 6);   // сиреневый: не совпадает с зонами и пометками (4) и областями загружения (2)
                    var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    var target = (LayerTableRecord)tr.GetObject(layers[layerName], OpenMode.ForRead);
                    if (target.IsLocked) throw new InvalidOperationException($"Слой {layerName} заблокирован.");

                    foreach (var entity in taken)
                    {
                        entity.UpgradeOpen();
                        entity.Layer = layerName;
                    }
                    tr.Commit();
                    ed.WriteMessage($"\nLIRLINE: принято отрезков {taken.Count}, нагрузка {load:g} т/м, слой {layerName}.\n"
                        + "Сетка не изменена. При LIREXPORT вдоль каждого отрезка встанут стержни КЭ 10 с околонулевой\n"
                        + "жёсткостью (S0 1 1 1, RO 1e-008) по УЗЛАМ СЕТКИ, и нагрузка ляжет на них.\n"
                        + "Отрезок обязан идти по узлам: узлы ищутся на нём самом, между соседними ставится стержень.\n"
                        + "Если узлов на отрезке меньше двух, экспорт про него предупредит и пропустит.\n");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка LIRLINE: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // Отрезки линейной нагрузки со всех слоёв MESH_LINELOAD(Q-...).
        // Полилиния даёт свои звенья по отдельности: каждое станет своей цепочкой
        // стержней. Интенсивность — из имени слоя.
        private List<LineLoadSeg> GetLineLoads(Transaction tr, Database db, out int skipped)
        {
            skipped = 0;
            var result = new List<LineLoadSeg>();
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || !IsLineLoadLayer(entity.Layer)) continue;
                var match = LineLoadValueRegex.Match(entity.Layer);
                double load = match.Success ? ParseLayerNumber(match.Groups[1].Value) : 0;
                if (!SlabZoneCore.Finite(load) || load <= 0) { skipped++; continue; }

                if (entity is Line line)
                {
                    if (Math.Abs(line.StartPoint.Z) > MeshTol.OnSegment
                        || Math.Abs(line.EndPoint.Z) > MeshTol.OnSegment) { skipped++; continue; }
                    var a = new Point2d(line.StartPoint.X, line.StartPoint.Y);
                    var b = new Point2d(line.EndPoint.X, line.EndPoint.Y);
                    if (a.GetDistanceTo(b) <= MeshTol.NodeMerge) { skipped++; continue; }
                    result.Add(new LineLoadSeg { A = a, B = b, LoadTm = load });
                }
                else if (entity is Polyline pline)
                {
                    if (!IsPolylineFlatXY(pline) || PolylineHasArcs(pline)) { skipped++; continue; }
                    var pts = GetPolylineVertices(pline);
                    int count = pline.Closed ? pts.Count : pts.Count - 1;
                    for (int i = 0; i < count; i++)
                    {
                        Point2d a = pts[i], b = pts[(i + 1) % pts.Count];
                        if (a.GetDistanceTo(b) <= MeshTol.NodeMerge) continue;
                        result.Add(new LineLoadSeg { A = a, B = b, LoadTm = load });
                    }
                }
            }
            return result;
        }
    }
}
