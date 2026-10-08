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
        private sealed class LocalSplitObject
        {
            public ObjectId Id;
            public readonly List<int> Edges = new List<int>();
        }

        [CommandMethod("LIRSPLIT")]
        public void LocalSplitCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "LIRSPLIT");
            Database db = doc.Database;
            try
            {
                var options = new PromptSelectionOptions();
                options.MessageForAdding = "\nВыберите участок готовой сетки для разбиения пересечений: ";
                options.AllowDuplicates = false;
                var filter = new SelectionFilter(new[] {
                    new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE"),
                    new TypedValue((int)DxfCode.LayerName, TriangulationLayerName)
                });
                var selection = ed.GetSelection(options, filter);
                if (selection.Status != PromptStatus.OK) return;
                var selectedIds = new HashSet<ObjectId>(selection.Value.GetObjectIds());
                SaveDrawingBeforeWork(doc, ed);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    var layer = (LayerTableRecord)tr.GetObject(layers[TriangulationLayerName], OpenMode.ForRead);
                    if (layer.IsLocked)
                    {
                        ed.WriteMessage($"\nСлой {TriangulationLayerName} заблокирован. Разблокируйте его и повторите.\n");
                        return;
                    }
                    var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                    var segments = new List<Point2d[]>();
                    var selected = new HashSet<int>();
                    var objects = new List<LocalSplitObject>();
                    int skipped = 0;
                    foreach (ObjectId id in space)
                    {
                        var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (entity == null || entity.Layer != TriangulationLayerName) continue;
                        var vertices = new List<Point2d>();
                        bool closed = false;
                        if (entity is Line line
                            && Math.Abs(line.StartPoint.Z) <= MeshTol.OnSegment
                            && Math.Abs(line.EndPoint.Z) <= MeshTol.OnSegment)
                        {
                            vertices.Add(new Point2d(line.StartPoint.X, line.StartPoint.Y));
                            vertices.Add(new Point2d(line.EndPoint.X, line.EndPoint.Y));
                        }
                        else if (entity is Polyline poly && IsPolylineFlatXY(poly) && !PolylineHasArcs(poly))
                        {
                            vertices = GetPolylineVertices(poly);
                            closed = poly.Closed;
                        }
                        else
                        {
                            if (selectedIds.Contains(id))
                            {
                                ed.WriteMessage("\nВыбрана сетка с дугами или вне плоскости XY. Разбиение отменено.\n");
                                return;
                            }
                            skipped++;
                            continue;
                        }
                        var item = new LocalSplitObject { Id = id };
                        int count = closed ? vertices.Count : vertices.Count - 1;
                        for (int k = 0; k < count; k++)
                        {
                            int edge = segments.Count;
                            segments.Add(new[] { vertices[k], vertices[(k + 1) % vertices.Count] });
                            item.Edges.Add(edge);
                            if (selectedIds.Contains(id)) selected.Add(edge);
                        }
                        objects.Add(item);
                    }
                    if (selected.Count == 0)
                    {
                        ed.WriteMessage($"\nВ выборе нет рабочих рёбер слоя {TriangulationLayerName}.\n");
                        return;
                    }

                    var cuts = LocalSplitCore.Calculate(segments, selected);
                    int changedObjects = 0, cutCount = 0, addedLines = 0;
                    foreach (var item in objects)
                    {
                        bool changed = false;
                        foreach (int edge in item.Edges) if (cuts.ContainsKey(edge)) { changed = true; break; }
                        if (!changed) continue;
                        var entity = (Entity)tr.GetObject(item.Id, OpenMode.ForWrite);
                        if (entity is Line line)
                        {
                            var edgeCuts = cuts[item.Edges[0]];
                            Point3d start = line.StartPoint, end = line.EndPoint;
                            // Клоны сохраняют цвет, тип линии, XData и метку LIRBUILD.
                            // Первый кусок остаётся исходным объектом с тем же ObjectId.
                            space.UpgradeOpen();
                            for (int k = 0; k < edgeCuts.Count; k++)
                            {
                                using (var piece = (Line)line.Clone())
                                {
                                    piece.StartPoint = LocalSplitPoint3d(start, end, edgeCuts[k]);
                                    piece.EndPoint = k + 1 < edgeCuts.Count
                                        ? LocalSplitPoint3d(start, end, edgeCuts[k + 1]) : end;
                                    space.AppendEntity(piece);
                                    tr.AddNewlyCreatedDBObject(piece, true);
                                }
                                addedLines++;
                            }
                            line.EndPoint = LocalSplitPoint3d(start, end, edgeCuts[0]);
                            cutCount += edgeCuts.Count;
                        }
                        else if (entity is Polyline poly)
                        {
                            // С конца: добавление вершин не сдвигает индексы ещё не обработанных рёбер.
                            // Полилиния сохраняется целиком, включая замкнутость и ширины.
                            for (int k = item.Edges.Count - 1; k >= 0; k--)
                            {
                                List<LocalSplitCut> edgeCuts;
                                if (!cuts.TryGetValue(item.Edges[k], out edgeCuts)) continue;
                                double w0 = poly.GetStartWidthAt(k), w1 = poly.GetEndWidthAt(k);
                                poly.SetEndWidthAt(k, w0 + (w1 - w0) * edgeCuts[0].Parameter);
                                for (int c = edgeCuts.Count - 1; c >= 0; c--)
                                {
                                    double t1 = edgeCuts[c].Parameter;
                                    double t2 = c + 1 < edgeCuts.Count ? edgeCuts[c + 1].Parameter : 1;
                                    poly.AddVertexAt(k + 1, edgeCuts[c].Point, 0,
                                        w0 + (w1 - w0) * t1, w0 + (w1 - w0) * t2);
                                }
                                cutCount += edgeCuts.Count;
                            }
                        }
                        changedObjects++;
                    }
                    tr.Commit();
                    ed.WriteMessage($"\nLIRSPLIT: выбрано рёбер {selected.Count}, разбито рёбер {cuts.Count}, разрезов {cutCount}, изменено объектов {changedObjects}, добавлено отрезков {addedLines}.\n");
                    if (cuts.Count == 0) ed.WriteMessage("Пересечений, требующих разбиения, нет. Сетка не изменена.\n");
                    if (skipped > 0) ed.WriteMessage($"Пропущено объектов сетки с неподдерживаемой геометрией: {skipped}.\n");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка LIRSPLIT: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        private static Point3d LocalSplitPoint3d(Point3d start, Point3d end, LocalSplitCut cut)
        {
            return new Point3d(cut.Point.X, cut.Point.Y, start.Z + (end.Z - start.Z) * cut.Parameter);
        }
    }
}
