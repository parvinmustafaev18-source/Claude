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
        private sealed class SlabZoneSideMark
        {
            public Point2d A, B;
            public int Side;
            public string Handle;
        }

        [CommandMethod("LIRZONE")]
        public void SlabZoneCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;
            EchoCommandStart(ed, "LIRZONE");
            SlabZoneSideMark failedSide = null;
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
                        {
                            failedSide = new SlabZoneSideMark
                            {
                                A = polygon[side], B = polygon[(side + 1) % polygon.Count],
                                Side = side + 1, Handle = id.Handle.ToString()
                            };
                            throw new InvalidOperationException($"Контур {id.Handle}: сторона {side + 1} не проходит по рёбрам готовой сетки. Контур не назначен.");
                        }
                        contours.Add(polyline);
                    }
                    string layerName = SlabZoneLayerPrefix + thickness.ToString("0.###", CultureInfo.InvariantCulture) + ")";
                    EnsureLayer(db, tr, layerName, 4);
                    var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    var target = (LayerTableRecord)tr.GetObject(layers[layerName], OpenMode.ForRead);
                    if (target.IsLocked) throw new InvalidOperationException($"Слой {layerName} заблокирован.");
                    if (layers.Has(SlabZoneMarkLayerName))
                    {
                        var markLayer = (LayerTableRecord)tr.GetObject(layers[SlabZoneMarkLayerName], OpenMode.ForRead);
                        if (!markLayer.IsLocked) EraseMarksOnLayer(tr, db, SlabZoneMarkLayerName);
                    }
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
                if (failedSide != null)
                {
                    // Основная транзакция уже Dispose/Abort: отметка переживает отказ,
                    // а слои исходных контуров и назначение толщины не меняются.
                    try
                    {
                        MarkSlabZoneSide(db, ed, failedSide);
                        ed.WriteMessage($"\nСторона {failedSide.Side} отмечена красной линией и кругами на концах, слой {SlabZoneMarkLayerName}.\n"
                            + $"Начало: X={failedSide.A.X:0.###}, Y={failedSide.A.Y:0.###}; конец: X={failedSide.B.X:0.###}, Y={failedSide.B.Y:0.###} мм.\n"
                            + "Исправьте совпадение с рёбрами сетки и повторите LIRZONE.\n");
                    }
                    catch (System.Exception markError)
                    {
                        ed.WriteMessage($"\nНе удалось показать сторону {failedSide.Side}: {markError.Message}\n"
                            + $"Координаты концов: ({failedSide.A.X:0.###}, {failedSide.A.Y:0.###}) → ({failedSide.B.X:0.###}, {failedSide.B.Y:0.###}) мм.\n");
                    }
                }
            }
        }

        private void MarkSlabZoneSide(Database db, Editor ed, SlabZoneSideMark mark)
        {
            Extents3d bounds;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureLayer(db, tr, SlabZoneMarkLayerName, 1);
                var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var layer = (LayerTableRecord)tr.GetObject(layers[SlabZoneMarkLayerName], OpenMode.ForRead);
                if (layer.IsLocked) throw new InvalidOperationException($"Разблокируйте слой {SlabZoneMarkLayerName}.");
                layer.UpgradeOpen();
                layer.IsOff = false;
                layer.IsFrozen = false;
                layer.IsPlottable = false;
                EraseMarksOnLayer(tr, db, SlabZoneMarkLayerName);
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                var a = new Point3d(mark.A.X, mark.A.Y, 0);
                var b = new Point3d(mark.B.X, mark.B.Y, 0);
                var line = new Line(a, b)
                {
                    Layer = SlabZoneMarkLayerName, ColorIndex = 1, LineWeight = LineWeight.LineWeight035
                };
                space.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);
                bounds = line.GeometricExtents;
                double radius = Math.Min(ProblemMarkRadius, mark.A.GetDistanceTo(mark.B) / 4.0);
                foreach (var point in new[] { a, b })
                {
                    var circle = new Circle(point, Vector3d.ZAxis, radius)
                    {
                        Layer = SlabZoneMarkLayerName, ColorIndex = 1, LineWeight = LineWeight.LineWeight035
                    };
                    space.AppendEntity(circle);
                    tr.AddNewlyCreatedDBObject(circle, true);
                    bounds.AddExtents(circle.GeometricExtents);
                }
                var text = new DBText
                {
                    TextString = $"сторона {mark.Side}, контур {mark.Handle}",
                    Height = ProblemTextHeight, Layer = SlabZoneMarkLayerName, ColorIndex = 1,
                    TextStyleId = EnsurePluginTextStyle(db, tr),
                    Position = new Point3d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0 + ProblemMarkRadius + ProblemTextHeight, 0)
                };
                space.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                bounds.AddExtents(text.GeometricExtents);
                tr.Commit();
            }
            // WCS -> DCS: приближение работает и на повёрнутом виде плана.
            try
            {
                using (ViewTableRecord view = ed.GetCurrentView())
                {
                    if (!view.PerspectiveEnabled)
                    {
                        Matrix3d transform = Matrix3d.PlaneToWorld(view.ViewDirection);
                        transform = Matrix3d.Displacement(view.Target - Point3d.Origin) * transform;
                        transform = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * transform;
                        bounds.TransformBy(transform.Inverse());
                        double w = Math.Max(bounds.MaxPoint.X - bounds.MinPoint.X, ProblemMarkRadius) * 1.2;
                        double h = Math.Max(bounds.MaxPoint.Y - bounds.MinPoint.Y, ProblemMarkRadius) * 1.2;
                        double aspect = view.Width / Math.Max(view.Height, MeshTol.Zero);
                        if (w / h > aspect) h = w / aspect; else w = h * aspect;
                        view.CenterPoint = new Point2d((bounds.MinPoint.X + bounds.MaxPoint.X) / 2.0,
                            (bounds.MinPoint.Y + bounds.MaxPoint.Y) / 2.0);
                        view.Width = w;
                        view.Height = h;
                        ed.SetCurrentView(view);
                    }
                }
                ed.Regen();
            }
            catch (System.Exception)
            {
                // Отметка уже сохранена; отказ изменения вида её не отменяет.
            }
        }

        private List<Point2d> ReadSlabZonePolygon(Polyline polyline)
        {
            string reason = PolylineContourReason(polyline);
            if (reason != null)
                throw new InvalidOperationException($"Контур зоны {polyline.ObjectId.Handle}: {reason}.");
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
