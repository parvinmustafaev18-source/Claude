using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    // ПЛИТА ДРУГОЙ ТОЛЩИНЫ НА УЧАСТКЕ.
    //
    // Под пилонами, в зонах продавливания и под стенами плита бывает другой
    // толщины, чем вокруг: та же плита, тот же уровень, но в пределах замкнутого
    // контура толщина своя (обычно больше, но правило её не ограничивает).
    // В ЛИРУ это уходит как ОТДЕЛЬНАЯ жёсткость пластин — геометрия схемы не
    // меняется, меняется только номер жёсткости у элементов внутри зоны.
    //
    // Контур и сетку задаёт инженер. LIRTHICK назначает только слой с толщиной;
    // экспорт выбирает жёсткость по центру готового элемента плиты.
    internal class ThickZone
    {
        public List<Point2d> Poly = new List<Point2d>();   // контур зоны (против часовой)
        public double ThicknessMm;                          // толщина плиты в зоне, мм
    }

    public partial class Commands
    {
        // Слой участка: MESH_THICK(H-<толщина>), по слою на каждую толщину.
        // Семейство то же, что MESH_HOLES и MESH_PYLONS: такие контуры читаются
        // ПО СЛОЮ, без указания мышью, и толщина живёт в имени слоя — как у
        // FOUNDATION_SLABS(H-...). Префикс начинается с MESH_, поэтому слой
        // автоматически попадает под IsMarkLayer и LIRLAYERS его не уводит.
        private const string ThickLayerPrefix = "MESH_THICK(";
        private const short ThickLayerColor = 5;   // синий: не совпадает с отверстиями (6) и пилонами (8)

        private static bool IsThickLayer(string layer)
        {
            return !string.IsNullOrEmpty(layer) && layer.StartsWith(ThickLayerPrefix);
        }

        // Замкнутые контуры участков со всех слоёв MESH_THICK(H-...). Толщина —
        // из имени слоя; слой без "H-" пропускается: безымянная толщина в ЛИРУ
        // не уйдёт, а молча подставлять толщину плиты опаснее, чем пропустить.
        private List<ThickZone> GetThickZones(Transaction tr, Database db, out int skippedOpen, out int skippedNoThickness)
        {
            var result = new List<ThickZone>();
            skippedOpen = 0;
            skippedNoThickness = 0;
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || !IsThickLayer(ent.Layer)) continue;

                Polyline pl = ent as Polyline;
                if (pl == null) continue;
                if (!pl.Closed) { skippedOpen++; continue; }

                double t;
                if (!TryParseLayerHeight(ent.Layer, out t) || t <= 0) { skippedNoThickness++; continue; }

                var verts = GetPolylineVertices(pl);
                if (verts.Count < 3) continue;
                EnsureCcw(verts);
                result.Add(new ThickZone { Poly = verts, ThicknessMm = t });
            }

            return result;
        }

        // Регистрация участков без изменения готовой сетки.
        // Границы элементов инженер совмещает с контурами вручную.
        [CommandMethod("LIRTHICK")]
        public void ThickZoneCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "LIRTHICK");
            Database db = doc.Database;

            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите контуры участков плиты другой толщины (замкнутые полилинии): ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            PromptDoubleOptions pdoT = new PromptDoubleOptions("\nТолщина плиты на этом участке, мм: ");
            pdoT.DefaultValue = 500.0;
            pdoT.AllowNegative = false;
            pdoT.AllowZero = false;
            PromptDoubleResult pdrT = ed.GetDouble(pdoT);
            if (pdrT.Status != PromptStatus.OK) return;
            double thickness = pdrT.Value;

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                string zoneLayer = ThickLayerPrefix + "H-"
                    + thickness.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")";
                var contour = GetSlabContour(tr, db);
                EnsureLayer(db, tr, zoneLayer, ThickLayerColor);

                int taken = 0, skippedOpen = 0, skippedArcs = 0, skippedService = 0;
                int skippedInvalid = 0, skippedOutside = 0;
                foreach (SelectedObject so in psr.Value)
                {
                    Polyline pl = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Polyline;
                    if (pl == null) continue;

                    // Чужие служебные контуры (отверстия, пилоны, сам контур плиты)
                    // в участок другой толщины не превращаем: такой «перенос» молча сломал бы
                    // чтение этих слоёв другими командами.
                    if (!IsThickLayer(pl.Layer) &&
                        (pl.Layer == HoleLayerName || pl.Layer == PylonOutlineLayerName
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
                    taken++;
                }

                ed.WriteMessage($"\nКонтуров принято на слой {zoneLayer}: {taken}" +
                    (skippedOpen > 0 ? $", пропущено незамкнутых: {skippedOpen}" : "") +
                    (skippedArcs > 0 ? $", пропущено с дугами (ЛИРА дуги не принимает): {skippedArcs}" : "") +
                    (skippedService > 0 ? $", пропущено служебных контуров (отверстия/пилоны/плита): {skippedService}" : "") +
                    (skippedInvalid > 0 ? $", пропущено вырожденных/самопересекающихся: {skippedInvalid}" : "") +
                    (skippedOutside > 0 ? $", пропущено участков вне плиты: {skippedOutside}" : "") + "\n");

                if (taken == 0)
                {
                    ed.WriteMessage("\nНи одного пригодного контура — сетка не тронута.\n");
                    tr.Commit();
                    return;
                }

                if (contour == null)
                    ed.WriteMessage("\nВНИМАНИЕ: контур плиты не найден — проверьте, что участки целиком лежат внутри плиты.\n");

                ed.WriteMessage("\nГотово. Контуры получили толщину; сетка не изменена. Совместите границы участков с рёбрами сетки вручную.\n");
                ed.WriteMessage("LIREXPORT назначит элементам плиты жёсткость по толщине участка, в который попал центр элемента. Пилоны сохранят свои жёсткости.\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка LIRTHICK: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // Контур плиты из слоя FOUNDATION_SLABS(H-...): нужен как неподвижная
        // граница и для проверки «зона внутри плиты». Если контуров несколько,
        // берётся самый большой по площади — остальные это, как правило, внутренние
        // контуры-пояснения. null — контура в чертеже нет (LIRLAYERS не запускали).
        private List<Point2d> GetSlabContour(Transaction tr, Database db)
        {
            List<Point2d> best = null;
            double bestArea = 0;
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || !IsSlabLayer(ent.Layer)) continue;

                Polyline pl = ent as Polyline;
                if (pl == null || !pl.Closed) continue;

                var verts = GetPolylineVertices(pl);
                if (verts.Count < 3) continue;

                double area = Math.Abs(PolygonArea(verts));
                if (area > bestArea) { bestArea = area; best = verts; }
            }

            if (best != null) EnsureCcw(best);
            return best;
        }

        // Чтение линий сетки вместе с их объектами. Параллельные списки: на каждый
        // отрезок — индекс объекта, которому он принадлежит. Полилиния даёт
        // несколько отрезков с одним владельцем, поэтому при изменении хотя бы
        // одного её куска переписывается вся полилиния целиком (см. ApplyMeshSegments).
        private void ReadMeshSegments(
            Transaction tr, Database db,
            List<ObjectId> ents, List<Point2d[]> segs, List<int> owner,
            List<ObjectId> skip = null)
        {
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                // Указанный пользователем контур (области, зоны) мог оказаться в том же
                // слое: читать его как линию сетки нельзя, иначе команда его же и сотрёт.
                if (skip != null && skip.Contains(id)) continue;

                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || ent.Layer != TriangulationLayerName) continue;

                if (ent is Line ln)
                {
                    int idx = ents.Count;
                    ents.Add(id);
                    segs.Add(new Point2d[] {
                        new Point2d(ln.StartPoint.X, ln.StartPoint.Y),
                        new Point2d(ln.EndPoint.X, ln.EndPoint.Y) });
                    owner.Add(idx);
                }
                else if (ent is Polyline pl)
                {
                    var verts = GetPolylineVertices(pl);
                    int count = pl.Closed ? verts.Count : verts.Count - 1;
                    if (count <= 0) continue;
                    int idx = ents.Count;
                    ents.Add(id);
                    for (int i = 0; i < count; i++)
                    {
                        segs.Add(new Point2d[] { verts[i], verts[(i + 1) % verts.Count] });
                        owner.Add(idx);
                    }
                }
            }
        }

        // Ключ отрезка для сравнения «было/стало»: пара концов, округлённая до
        // сотой миллиметра и упорядоченная (ребро A-B и ребро B-A — одно ребро).
        // Округление грубее допуска слияния узлов (1e-3 мм) намеренно: разрез и
        // перестроение списков дают расхождение в последних знаках мантиссы, и без
        // огрубления нетронутые отрезки выглядели бы изменившимися.
        private static string SegmentKey(Point2d a, Point2d b)
        {
            long ax = (long)Math.Round(a.X * 100.0), ay = (long)Math.Round(a.Y * 100.0);
            long bx = (long)Math.Round(b.X * 100.0), by = (long)Math.Round(b.Y * 100.0);
            bool swap = bx < ax || (bx == ax && by < ay);
            return swap
                ? bx + "," + by + ";" + ax + "," + ay
                : ax + "," + ay + ";" + bx + "," + by;
        }

        // ЗАПИСЬ РЕЗУЛЬТАТА В ЧЕРТЁЖ ПО РАЗНИЦЕ.
        //
        // Перерисовывать всю сетку целиком было бы проще, но тогда каждая линия,
        // начерченная инженером вручную, превратилась бы в линию плагина (с меткой
        // XData, по которой следующий LIRBUILD её сотрёт) и потеряла бы свой цвет,
        // тип линии и вес. Поэтому пишется только разница: объект, все отрезки
        // которого есть в результате, остаётся в чертеже нетронутым; объект, чья
        // геометрия изменилась, стирается, а недостающие отрезки дорисовываются.
        private void ApplyMeshSegments(
            Transaction tr, Database db,
            List<ObjectId> ents, List<Point2d[]> oldSegs, List<int> owner,
            List<Point2d[]> newSegs,
            out int erased, out int added, out int kept)
        {
            erased = 0; added = 0; kept = 0;

            // Непотраченные отрезки результата: ключ -> стек индексов. Стек, а не
            // флаг: одинаковых по ключу отрезков в сетке быть не должно, но если
            // они всё же есть, каждый объект заберёт свой.
            var pool = new Dictionary<string, Stack<int>>();
            for (int i = 0; i < newSegs.Count; i++)
            {
                string k = SegmentKey(newSegs[i][0], newSegs[i][1]);
                Stack<int> st;
                if (!pool.TryGetValue(k, out st)) { st = new Stack<int>(); pool[k] = st; }
                st.Push(i);
            }

            // Отрезки каждого объекта
            var byEnt = new List<List<int>>();
            for (int i = 0; i < ents.Count; i++) byEnt.Add(new List<int>());
            for (int i = 0; i < oldSegs.Count; i++) byEnt[owner[i]].Add(i);

            var takenByKeep = new List<int>();
            for (int e = 0; e < ents.Count; e++)
            {
                takenByKeep.Clear();
                bool all = true;

                foreach (int si in byEnt[e])
                {
                    string k = SegmentKey(oldSegs[si][0], oldSegs[si][1]);
                    Stack<int> st;
                    if (pool.TryGetValue(k, out st) && st.Count > 0)
                        takenByKeep.Add(st.Pop());
                    else { all = false; break; }
                }

                if (all && byEnt[e].Count > 0)
                {
                    kept += byEnt[e].Count;
                    continue;   // объект не трогаем вовсе: его геометрия в результате есть
                }

                // Откат: взятые куски возвращаются в пул, объект уходит под снос.
                foreach (int ri in takenByKeep)
                    pool[SegmentKey(newSegs[ri][0], newSegs[ri][1])].Push(ri);

                Entity ent = tr.GetObject(ents[e], OpenMode.ForWrite) as Entity;
                if (ent != null && !ent.IsErased) { ent.Erase(); erased++; }
            }

            // Всё, что осталось в пуле, — новая геометрия. Без записи в таблицу
            // RegApp метка XData к новым отрезкам не привяжется — молча, без ошибки,
            // и следующий LIRBUILD не узнает в них свою сетку.
            EnsureRegApp(db, tr, MeshXDataApp);
            EnsureLayer(db, tr, TriangulationLayerName, PickRandomColor(new Random(), GetUsedLayerColors(db, tr)));
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            foreach (var kv in pool)
            {
                foreach (int ri in kv.Value)
                {
                    DrawSegment(ms, tr, newSegs[ri][0], newSegs[ri][1]);
                    added++;
                }
            }
        }
    }
}
