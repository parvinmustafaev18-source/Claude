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
    // ОБЛАСТЬ РАВНОМЕРНО РАСПРЕДЕЛЁННОЙ НАГРУЗКИ: контур, внутри которого пластины
    // плиты получают нагрузку в т/м². Жёсткости при этом НЕ трогаются вовсе —
    // нагрузка уходит отдельными документами 6 и 7 текстового формата ЛИРЫ,
    // а документы 0, 1, 3 и 4 остаются теми же до байта (проверено сравнением
    // двух задач, сохранённых самой ЛИРОЙ: с нагрузкой и без).
    //
    // Устроена как LIRMARK, а не как LIRZONE: граница области рисуется от руки и
    // по рёбрам сетки идти не обязана. Попадание решает ДОЛЯ ПЛОЩАДИ элемента
    // внутри контура (MeshTol.MarkAreaShare) — нагрузка в ЛИРЕ задаётся на весь
    // элемент целиком, частичной не бывает, поэтому порог и решает, кому достаётся
    // вся нагрузка. Сетка командой не меняется ни на одно ребро.
    internal sealed class LoadZone
    {
        public List<Point2d> Poly;
        public double LoadTm2;   // интенсивность, т/м² (единицы задачи)
    }

    public partial class Commands
    {
        [CommandMethod("LIRLOAD")]
        public void LoadZoneCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;
            EchoCommandStart(ed, "LIRLOAD");
            try
            {
                var options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nВыберите замкнутые контуры областей загружения: ",
                    AllowDuplicates = false
                };
                var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
                var selection = ed.GetSelection(options, filter);
                if (selection.Status != PromptStatus.OK) return;

                // Единицы силы в задаче — тонны, длины — метры (документ 0:
                // "33;M 1 CM 100 T 1 C 1 /"), поэтому интенсивность спрашивается
                // в т/м²: ровно в том виде, в каком она уйдёт в файл и покажется
                // в ЛИРЕ. Пересчёта из кПа нет намеренно — это было бы ещё одно
                // место, где можно ошибиться на порядок.
                var prompt = new PromptDoubleOptions("\nРавномерно распределённая нагрузка, т/м²: ")
                {
                    DefaultValue = 0.5, AllowNegative = false, AllowZero = false
                };
                var answer = ed.GetDouble(prompt);
                if (answer.Status != PromptStatus.OK) return;
                double load = Math.Round(answer.Value, 4);
                if (!SlabZoneCore.Finite(load) || load <= 0)
                {
                    ed.WriteMessage("\nНужна положительная конечная нагрузка с точностью до 0.0001 т/м².\n");
                    return;
                }

                string layerName = LoadZoneLayerPrefix
                    + load.ToString("0.####", CultureInfo.InvariantCulture) + ")";

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var contours = new List<Polyline>();
                    foreach (ObjectId id in selection.Value.GetObjectIds())
                    {
                        var polyline = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                        if (polyline == null || polyline.OwnerId != db.CurrentSpaceId)
                            throw new InvalidOperationException("Выбирайте контуры в текущем пространстве чертежа.");
                        if (IsServiceLayer(polyline.Layer) && !IsLoadZoneLayer(polyline.Layer))
                            throw new InvalidOperationException($"Контур {id.Handle} на служебном слое {polyline.Layer}. Начертите отдельный контур области.");
                        var sourceLayer = (LayerTableRecord)tr.GetObject(polyline.LayerId, OpenMode.ForRead);
                        if (sourceLayer.IsLocked)
                            throw new InvalidOperationException($"Слой {polyline.Layer} заблокирован.");
                        string reason = PolylineContourReason(polyline);
                        if (reason != null)
                            throw new InvalidOperationException($"Контур {id.Handle}: {reason}.");
                        var poly = GetPolylineVertices(polyline);
                        if (!SlabZoneCore.ValidatePolygon(poly, out string error))
                            throw new InvalidOperationException($"Контур {id.Handle}: {error}.");
                        contours.Add(polyline);
                    }

                    EnsureLayer(db, tr, layerName, 2);   // жёлтый: не совпадает с зонами и пометками (4)
                    var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    var target = (LayerTableRecord)tr.GetObject(layers[layerName], OpenMode.ForRead);
                    if (target.IsLocked) throw new InvalidOperationException($"Слой {layerName} заблокирован.");

                    foreach (var polyline in contours)
                    {
                        polyline.UpgradeOpen();
                        polyline.Layer = layerName;
                    }
                    tr.Commit();
                    ed.WriteMessage($"\nLIRLOAD: принято контуров {contours.Count}, нагрузка {load:g} т/м², слой {layerName}.\n"
                        + "Сетка не изменена. При LIREXPORT нагрузку получит элемент плиты, внутри контура которого\n"
                        + $"лежит не меньше {MeshTol.MarkAreaShare * 100:0.#}% его площади — целиком внутрь он укладываться не обязан.\n"
                        + "Жёсткости не меняются: нагрузка уходит отдельными документами 6 и 7, загружение 1.\n"
                        + "Сумма нагрузки по областям печатается при экспорте и пишется в файл легенды — сверьте её с ЛИРОЙ.\n");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка LIRLOAD: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // Контуры областей загружения со всех слоёв MESH_LOAD(Q-...). Интенсивность —
        // из имени слоя; слой без разбираемого числа пропускается: безымянная
        // нагрузка в ЛИРУ не уйдёт, а подставлять значение наугад опаснее.
        private List<LoadZone> GetLoadZones(Transaction tr, Database db, out int skipped)
        {
            skipped = 0;
            var result = new List<LoadZone>();
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || !IsLoadZoneLayer(entity.Layer)) continue;
                var match = LoadZoneValueRegex.Match(entity.Layer);
                double load = match.Success ? ParseLayerNumber(match.Groups[1].Value) : 0;
                if (!SlabZoneCore.Finite(load) || load <= 0) { skipped++; continue; }
                var polyline = entity as Polyline;
                if (polyline == null || PolylineContourReason(polyline) != null) { skipped++; continue; }
                var poly = GetPolylineVertices(polyline);
                if (!SlabZoneCore.ValidatePolygon(poly, out _)) { skipped++; continue; }
                result.Add(new LoadZone { Poly = poly, LoadTm2 = load });
            }
            return result;
        }
    }
}
