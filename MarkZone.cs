using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    // Помеченная область плиты: контур, внутри которого элементы получают ОТДЕЛЬНЫЙ
    // номер жёсткости с ТЕМИ ЖЕ параметрами, что у плиты. Расчёт от этого не
    // меняется ничем (E, толщина, RO, ν те же), а в ЛИРЕ область выделяется одним
    // щелчком фильтра по жёсткости — это и есть вся цель.
    //
    // Почему не LIRZONE с той же толщиной: он намеренно оставляет жёсткость №1,
    // когда толщина зоны равна толщине плиты (ExportCore.cs, блок зон), то есть
    // отдельного номера не даёт. И правило попадания у него другое — «целиком
    // внутри», из-за чего пограничные элементы остались бы непомеченными.
    internal sealed class MarkZone
    {
        public List<Point2d> Poly;
        public string Comment;
    }

    public partial class Commands
    {
        // В имени слоя AutoCAD запрещены эти символы, а запятая и точка с запятой
        // вдобавок разделяют имена в командах выбора слоёв. Комментарий едет в имя
        // слоя (видимое состояние вместо XData), поэтому проверяем сразу.
        private const string LayerNameForbidden = "<>/\\\":;?*|,=`";

        [CommandMethod("LIRMARK")]
        public void MarkZoneCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;
            EchoCommandStart(ed, "LIRMARK");
            try
            {
                var options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nВыберите замкнутые контуры областей, которые нужно помечать: ",
                    AllowDuplicates = false
                };
                var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
                var selection = ed.GetSelection(options, filter);
                if (selection.Status != PromptStatus.OK) return;

                var prompt = new PromptStringOptions("\nКомментарий к области (попадёт в легенду и в имя слоя): ")
                {
                    AllowSpaces = true
                };
                var answer = ed.GetString(prompt);
                if (answer.Status != PromptStatus.OK) return;
                string comment = answer.StringResult == null ? "" : answer.StringResult.Trim();
                if (comment.Length == 0)
                {
                    ed.WriteMessage("\nНужен непустой комментарий: по нему область опознаётся в легенде.\n");
                    return;
                }
                if (comment.IndexOfAny(LayerNameForbidden.ToCharArray()) >= 0)
                {
                    ed.WriteMessage($"\nВ комментарии нельзя использовать символы {LayerNameForbidden} — он идёт в имя слоя.\n");
                    return;
                }
                string layerName = MarkZoneLayerPrefix + comment + ")";
                if (layerName.Length > 255)
                {
                    ed.WriteMessage("\nКомментарий слишком длинный: имя слоя не должно превышать 255 знаков.\n");
                    return;
                }

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var contours = new List<Polyline>();
                    foreach (ObjectId id in selection.Value.GetObjectIds())
                    {
                        var polyline = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                        if (polyline == null || polyline.OwnerId != db.CurrentSpaceId)
                            throw new InvalidOperationException("Выбирайте контуры в текущем пространстве чертежа.");
                        if (IsServiceLayer(polyline.Layer) && !IsMarkZoneLayer(polyline.Layer))
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
                    ed.WriteMessage($"\nLIRMARK: помечено контуров {contours.Count}, слой {layerName}.\n"
                        + "Сетка не изменена. При LIREXPORT элемент плиты попадёт в область, если внутри контура\n"
                        + $"лежит не меньше {MeshTol.MarkAreaShare * 100:0.#}% его площади — целиком внутрь он укладываться не обязан.\n"
                        + "Такие элементы получат свой номер жёсткости с теми же параметрами, что у плиты: в ЛИРЕ\n"
                        + "их выделит фильтр по жёсткости, номер и комментарий будут в файле легенды. Элементы,\n"
                        + "у которых уже своя жёсткость (тело пилона, зона толщины), остаются как есть.\n");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка LIRMARK: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // Почему полилиния не годится как контур — ТОЧНАЯ причина вместо общей
        // фразы «нужна замкнутая полилиния без дуг в плоскости XY на отметке 0»:
        // у «не замкнута», «не в плоскости XY», «не на нуле» и «есть дуги»
        // способы лечения разные, а по общему тексту инженер их не различал.
        // Возвращает null, если контур годится.
        private string PolylineContourReason(Polyline pline)
        {
            if (!pline.Closed)
                return "полилиния не замкнута — свойство «Замкнуто» = Нет (PEDIT -> Замкнуть"
                    + " или Свойства -> Замкнуто = Да). Совпадения последней вершины с первой недостаточно";
            if (!pline.Normal.IsParallelTo(Vector3d.ZAxis) || pline.Normal.Z <= 0)
                return "полилиния лежит не в плоскости XY: своя ПСК или перевёрнутая нормаль"
                    + " (начертите контур в мировой ПСК, вид сверху)";
            if (Math.Abs(pline.Elevation) >= MeshTol.OnSegment)
                return $"полилиния на отметке Z = {pline.Elevation:0.###} мм, нужна 0"
                    + " (Свойства -> Высота = 0 либо переместите контур на нулевую отметку)";
            int n = pline.NumberOfVertices;
            for (int i = 0; i < n; i++)
                if (Math.Abs(pline.GetBulgeAt(i)) > MeshTol.Zero && (i < n - 1 || pline.Closed))
                    return $"в полилинии есть дуговой сегмент (вершина {i + 1}): дуги не допускаются"
                        + " — замените дугу ломаной";
            return null;
        }

        // Контуры помеченных областей со всех слоёв MESH_MARK(...). Комментарий —
        // то, что стоит в имени слоя между скобками: контуры с одинаковым
        // комментарием получат в выгрузке ОДИН номер жёсткости.
        private List<MarkZone> GetMarkZones(Transaction tr, Database db, out int skipped)
        {
            skipped = 0;
            var result = new List<MarkZone>();
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || !IsMarkZoneLayer(entity.Layer)) continue;
                var polyline = entity as Polyline;
                if (polyline == null || PolylineContourReason(polyline) != null)
                { skipped++; continue; }
                var poly = GetPolylineVertices(polyline);
                if (!SlabZoneCore.ValidatePolygon(poly, out _)) { skipped++; continue; }
                result.Add(new MarkZone { Poly = poly, Comment = MarkZoneComment(entity.Layer) });
            }
            return result;
        }
    }
}
