using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    // ОБЛАСТЬ РАВНОМЕРНО РАСПРЕДЕЛЁННОЙ НАГРУЗКИ.
    //
    // Инженер обводит замкнутым контуром участок плиты (кладовая, стяжка, грунт
    // на перекрытии) и задаёт интенсивность. В ЛИРУ это уходит нагрузкой на
    // ПЛАСТИНЫ: каждый элемент плиты, чей центр попал внутрь контура, получает
    // равномерную нагрузку в т/м². Геометрия схемы при этом не меняется —
    // добавляются только строки в документ нагрузок.
    //
    // Устройство намеренно повторяет LIRTHICK (ThickZone.cs): тот же способ
    // хранения (значение в имени слоя), тот же разрез сетки по границам зоны без
    // сдвига узлов, тот же признак «центр элемента внутри контура» в экспорте.
    // Разрез обязателен по той же причине, что и у толщины: нагрузка постоянна
    // НА ЭЛЕМЕНТ, и ячейка, которую граница зоны режет пополам, иначе была бы
    // нагружена целиком или не нагружена вовсе.
    internal class LoadZone
    {
        public List<Point2d> Poly = new List<Point2d>();   // контур зоны (против часовой)
        public double LoadTm2;                              // интенсивность, т/м² (единицы задачи)
    }

    public partial class Commands
    {
        // Слой области: MESH_LOAD(Q-<интенсивность>), по слою на каждое значение.
        // Префикс начинается с MESH_, поэтому слой попадает под IsMarkLayer и
        // LIRLAYERS его не уводит.
        private const string LoadLayerPrefix = "MESH_LOAD(";
        private const short LoadLayerColor = 2;   // жёлтый: не совпадает с утолщениями (5), отверстиями (6), пилонами (8)

        private static bool IsLoadLayer(string layer)
        {
            return !string.IsNullOrEmpty(layer) && layer.StartsWith(LoadLayerPrefix);
        }

        // Интенсивность из имени слоя: "MESH_LOAD(Q-0.5)" -> 0.5.
        private static readonly System.Text.RegularExpressions.Regex LayerLoadRegex =
            new System.Text.RegularExpressions.Regex(@"Q-([\d.,]+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static bool TryParseLayerLoad(string layer, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(layer)) return false;
            var m = LayerLoadRegex.Match(layer);
            if (!m.Success) return false;
            value = ParseLayerNumber(m.Groups[1].Value);
            return true;
        }

        // Замкнутые контуры областей со всех слоёв MESH_LOAD(Q-...). Интенсивность —
        // из имени слоя; слой без "Q-" пропускается: безымянная нагрузка в ЛИРУ не
        // уйдёт, а подставлять число наугад опаснее, чем пропустить область.
        private List<LoadZone> GetLoadZones(Transaction tr, Database db, out int skippedOpen, out int skippedNoValue)
        {
            var result = new List<LoadZone>();
            skippedOpen = 0;
            skippedNoValue = 0;
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || !IsLoadLayer(ent.Layer)) continue;

                Polyline pl = ent as Polyline;
                if (pl == null) continue;
                if (!pl.Closed) { skippedOpen++; continue; }

                double q;
                if (!TryParseLayerLoad(ent.Layer, out q) || q <= 0) { skippedNoValue++; continue; }

                var verts = GetPolylineVertices(pl);
                if (verts.Count < 3) continue;
                EnsureCcw(verts);
                result.Add(new LoadZone { Poly = verts, LoadTm2 = q });
            }

            return result;
        }

        // Контуры получают интенсивность; их границы разрезают готовую сетку.
        [CommandMethod("LIRLOAD")]
        public void LoadZoneCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "LIRLOAD");
            Database db = doc.Database;

            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите контуры областей загружения (замкнутые полилинии): ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            // Единицы силы в задаче — тонны (документ 0: "33;M 1 CM 100 T 1 C 1 /"),
            // длины — метры, поэтому интенсивность спрашивается в т/м²: ровно в том
            // виде, в каком она уйдёт в файл и покажется в ЛИРЕ. Пересчёта нет
            // намеренно — пересчёт был бы ещё одним местом, где можно ошибиться.
            PromptDoubleOptions pdoQ = new PromptDoubleOptions("\nРавномерно распределённая нагрузка, т/м²: ");
            pdoQ.DefaultValue = 0.5;
            pdoQ.AllowNegative = false;
            pdoQ.AllowZero = false;
            PromptDoubleResult pdrQ = ed.GetDouble(pdoQ);
            if (pdrQ.Status != PromptStatus.OK) return;
            double load = pdrQ.Value;

            SaveDrawingBeforeWork(doc, ed);

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                string zoneLayer = LoadLayerPrefix + "Q-"
                    + load.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ")";
                var contour = GetSlabContour(tr, db);
                EnsureLayer(db, tr, zoneLayer, LoadLayerColor);

                int taken = 0, skippedOpen = 0, skippedArcs = 0, skippedService = 0;
                int skippedInvalid = 0, skippedOutside = 0;
                var selectedZones = new List<LoadZone>();
                foreach (SelectedObject so in psr.Value)
                {
                    Polyline pl = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Polyline;
                    if (pl == null) continue;

                    // Чужие служебные контуры (отверстия, пилоны, участки другой
                    // толщины, сам контур плиты) в область загружения не превращаем:
                    // такой «перенос» молча сломал бы чтение этих слоёв другими командами.
                    if (!IsLoadLayer(pl.Layer) &&
                        (pl.Layer == HoleLayerName || pl.Layer == PylonOutlineLayerName
                         || IsThickLayer(pl.Layer)
                         || IsSlabLayer(pl.Layer) || IsWallLayer(pl.Layer) || IsColumnLayer(pl.Layer)))
                    { skippedService++; continue; }

                    if (!pl.Closed) { skippedOpen++; continue; }
                    if (PolylineHasArcs(pl)) { skippedArcs++; continue; }

                    var verts = GetPolylineVertices(pl);
                    if (verts.Count < 3 || Math.Abs(PolygonArea(verts)) < MeshTol.MinArea
                        || FindSelfIntersections(verts).Count > 0)
                    { skippedInvalid++; continue; }
                    if (contour != null && !IsPolygonInsideContour(verts, contour))
                    { skippedOutside++; continue; }

                    pl.Layer = zoneLayer;
                    selectedZones.Add(new LoadZone { Poly = verts, LoadTm2 = load });
                    taken++;
                }

                ed.WriteMessage($"\nКонтуров принято на слой {zoneLayer}: {taken}" +
                    (skippedOpen > 0 ? $", пропущено незамкнутых: {skippedOpen}" : "") +
                    (skippedArcs > 0 ? $", пропущено с дугами (ЛИРА дуги не принимает): {skippedArcs}" : "") +
                    (skippedService > 0 ? $", пропущено служебных контуров (отверстия/пилоны/утолщения/плита): {skippedService}" : "") +
                    (skippedInvalid > 0 ? $", пропущено вырожденных/самопересекающихся: {skippedInvalid}" : "") +
                    (skippedOutside > 0 ? $", пропущено областей вне плиты: {skippedOutside}" : "") + "\n");

                if (taken == 0)
                {
                    ed.WriteMessage("\nНи одного пригодного контура — сетка не тронута.\n");
                    tr.Commit();
                    return;
                }

                if (contour == null)
                    ed.WriteMessage("\nВНИМАНИЕ: контур плиты не найден — проверьте, что области целиком лежат внутри плиты.\n");

                var meshEnts = new List<ObjectId>();
                var meshSegs = new List<Point2d[]>();
                var meshOwner = new List<int>();
                ReadMeshSegments(tr, db, meshEnts, meshSegs, meshOwner);
                if (meshSegs.Count == 0)
                {
                    ed.WriteMessage("\nКонтуры получили нагрузку, но линий сетки нет. После LIRBUILD повторите LIRLOAD на этих контурах для разрезания сетки.\n");
                    tr.Commit();
                    return;
                }

                var boundary = new List<Point2d[]>();
                foreach (var zone in selectedZones)
                    for (int i = 0; i < zone.Poly.Count; i++)
                        boundary.Add(new Point2d[] { zone.Poly[i], zone.Poly[(i + 1) % zone.Poly.Count] });
                // В пустоты новые линии не добавляем. Отпечаток пилона-пластины
                // пустотой не является: его сетка режется, а нагрузка на тело пилона
                // попадёт так же, как на остальную плиту.
                var voids = GetHolePolygons(tr, db);
                voids.AddRange(GetColumnPolygons(tr, db));
                boundary = ClipSegmentsOutsideColumns(boundary, voids, out _, out _);

                int splitEdges, boundaryEdges;
                var split = SplitMeshAtThickBoundaries(meshSegs, boundary, out splitEdges, out boundaryEdges);
                var outlines = new List<List<Point2d>>(voids);
                if (contour != null) outlines.Add(contour);
                if (HasIsolatedThickBoundary(split, boundaryEdges, outlines))
                {
                    ed.WriteMessage("\nКонтуры получили нагрузку, но граница образует остров внутри ячейки или касается сетки только в одной точке. Сетка не изменена. Добавьте линии сетки через эту область вручную и повторите LIRLOAD.\n");
                    tr.Commit();
                    return;
                }
                int erased, added, kept;
                ApplyMeshSegments(tr, db, meshEnts, meshSegs, meshOwner, split, out erased, out added, out kept);
                ed.WriteMessage($"\nРазрезано существующих рёбер: {splitEdges}; добавлено рёбер по границам областей: {boundaryEdges}. Узлы не перемещались.\n");
                ed.WriteMessage($"Чертёж: сохранено отрезков {kept}, добавлено {added}, удалено исходных объектов {erased}.\n");
                ed.WriteMessage("LIREXPORT запишет нагрузку на пластины внутри областей в документ нагрузок и перечислит области в легенде.\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка LIRLOAD: {ex.Message}\nИзменения команды отменены.\n");
            }
        }
    }
}
