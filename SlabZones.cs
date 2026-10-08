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
    public partial class Commands
    {
        [CommandMethod("LIRZONE")]
        public void SlabZoneCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;
            EchoCommandStart(ed, "LIRZONE");
            try
            {
                var options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nВыберите замкнутые контуры зон по рёбрам готовой сетки: ",
                    AllowDuplicates = false
                };
                var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
                var selection = ed.GetSelection(options, filter);
                if (selection.Status != PromptStatus.OK) return;
                var prompt = new PromptDoubleOptions("\nТолщина плиты в выбранных зонах, мм: ")
                {
                    DefaultValue = 300.0, AllowNegative = false, AllowZero = false
                };
                var answer = ed.GetDouble(prompt);
                if (answer.Status != PromptStatus.OK) return;
                double thickness = Math.Round(answer.Value, 3);
                if (!SlabZoneCore.Finite(thickness) || thickness <= 0)
                {
                    ed.WriteMessage("\nНужна положительная конечная толщина с точностью до 0.001 мм.\n");
                    return;
                }
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var contours = new List<Polyline>();
                    var segments = ReadZoneBoundarySegments(tr, db);
                    var index = SlabZoneCore.IndexSegments(segments);
                    foreach (ObjectId id in selection.Value.GetObjectIds())
                    {
                        var polyline = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                        if (polyline == null || polyline.OwnerId != db.CurrentSpaceId)
                            throw new InvalidOperationException("Выбирайте контуры в текущем пространстве чертежа.");
                        if (IsServiceLayer(polyline.Layer) && !IsSlabZoneLayer(polyline.Layer))
                            throw new InvalidOperationException($"Контур {id.Handle} на служебном слое {polyline.Layer}. Начертите отдельный контур зоны.");
                        var sourceLayer = (LayerTableRecord)tr.GetObject(polyline.LayerId, OpenMode.ForRead);
                        if (sourceLayer.IsLocked)
                            throw new InvalidOperationException($"Слой {polyline.Layer} заблокирован.");
                        var polygon = ReadSlabZonePolygon(polyline);
                        if (!SlabZoneCore.BoundaryCovered(polygon, segments, index, out int side))
                            throw new InvalidOperationException($"Контур {id.Handle}: сторона {side + 1} не проходит по рёбрам готовой сетки. Контур не назначен.");
                        contours.Add(polyline);
                    }
                    string layerName = SlabZoneLayerPrefix + thickness.ToString("0.###", CultureInfo.InvariantCulture) + ")";
                    EnsureLayer(db, tr, layerName, 4);
                    var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    var target = (LayerTableRecord)tr.GetObject(layers[layerName], OpenMode.ForRead);
                    if (target.IsLocked) throw new InvalidOperationException($"Слой {layerName} заблокирован.");
                    foreach (var polyline in contours)
                    {
                        polyline.UpgradeOpen();
                        polyline.Layer = layerName;
                    }
                    tr.Commit();
                    ed.WriteMessage($"\nLIRZONE: назначено контуров {contours.Count}, толщина {thickness:g} мм, слой {layerName}.\nПри LIREXPORT толщину получат только элементы целиком внутри зоны; граница включена. Сетка не изменена.\n");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка LIRZONE: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        private List<Point2d> ReadSlabZonePolygon(Polyline polyline)
        {
            if (!polyline.Closed || !IsPolylineFlatXY(polyline) || PolylineHasArcs(polyline))
                throw new InvalidOperationException($"Контур зоны {polyline.ObjectId.Handle}: нужна замкнутая полилиния без дуг в плоскости XY на отметке 0.");
            var polygon = GetPolylineVertices(polyline);
            if (!SlabZoneCore.ValidatePolygon(polygon, out string error))
                throw new InvalidOperationException($"Контур зоны {polyline.ObjectId.Handle}: {error}.");
            return polygon;
        }

        // Тот же набор рёбер, что читает экспорт: сетка, оси стен и границы плит.
        // Контуры зон сюда не входят: они не могут подтверждать сами себя.
        private List<Point2d[]> ReadZoneBoundarySegments(Transaction tr, Database db)
        {
            var segments = new List<Point2d[]>();
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || (entity.Layer != TriangulationLayerName
                    && !IsWallLayer(entity.Layer) && !IsSlabLayer(entity.Layer))) continue;
                if (entity is Line line)
                {
                    if (Math.Abs(line.StartPoint.Z) > MeshTol.OnSegment || Math.Abs(line.EndPoint.Z) > MeshTol.OnSegment) continue;
                    segments.Add(new[] { new Point2d(line.StartPoint.X, line.StartPoint.Y),
                        new Point2d(line.EndPoint.X, line.EndPoint.Y) });
                }
                else if (entity is Polyline polyline && IsPolylineFlatXY(polyline) && !PolylineHasArcs(polyline))
                {
                    var points = GetPolylineVertices(polyline);
                    int count = polyline.Closed ? points.Count : points.Count - 1;
                    for (int i = 0; i < count; i++)
                        segments.Add(new[] { points[i], points[(i + 1) % points.Count] });
                }
            }
            return segments;
        }

        private List<SlabThicknessZone> ReadSlabZones(Transaction tr, Database db)
        {
            var zones = new List<SlabThicknessZone>();
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || !IsSlabZoneLayer(entity.Layer)) continue;
                var match = SlabZoneThicknessRegex.Match(entity.Layer);
                double thickness = match.Success ? ParseLayerNumber(match.Groups[1].Value) : 0;
                if (!SlabZoneCore.Finite(thickness) || thickness <= 0 || !(entity is Polyline polyline))
                    throw new InvalidOperationException($"Зона {id.Handle} на слое {entity.Layer}: нужен контур с положительной толщиной. Повторите LIRZONE.");
                zones.Add(new SlabThicknessZone(ReadSlabZonePolygon(polyline), thickness, id.Handle.ToString()));
            }
            return zones;
        }
    }
}
