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
    // Почему отдельная команда, а не этап построения. К моменту, когда участок
    // появляется, сетка уже построена и, как правило, поправлена инженером руками; гнать
    // LIRBUILD заново — значит выбросить эту работу. Поэтому LIRTHICK правит
    // ГОТОВУЮ сетку на месте: двигает к контуру узлы, которые к нему близко,
    // режет рёбра, которые его пересекают, и добавляет недостающие рёбра по самому
    // контуру. Всё, что от контура далеко, остаётся байт в байт прежним — в чертеже
    // такие отрезки даже не перерисовываются (см. ApplyMeshSegments).
    internal class ThickZone
    {
        public List<Point2d> Poly = new List<Point2d>();   // контур зоны (против часовой)
        public double ThicknessMm;                          // толщина плиты в зоне, мм
    }

    // Вход подтяжки сетки к контурам участков. Как и у MeshCore, здесь только
    // геометрия: ни Editor, ни Transaction, ни Database — чтобы расчёт можно было
    // прогнать без AutoCAD.
    internal class ThickFitInput
    {
        public List<Point2d[]> Segments = new List<Point2d[]>();   // текущие линии сетки
        public List<ThickZone> Zones = new List<ThickZone>();      // контуры участков
        public double Tolerance = 120.0;                            // допуск подтяжки узла, мм

        // Узлы, которые двигать НЕЛЬЗЯ: контуры пилонов (отпечаток обязан остаться
        // прежним — пилон от другой толщины плиты не меняется ничем), кромки
        // отверстий и контур самой плиты. Сдвинув такой узел, мы сломали бы чужое построение ради
        // косметики на границе зоны.
        public List<List<Point2d>> FixedPolys = new List<List<Point2d>>();

        // Контур плиты: участок обязан лежать внутри него (то же жёсткое
        // правило, что у отверстий). null — контура в чертеже не нашлось, проверка
        // пропускается с предупреждением.
        public List<Point2d> Contour;
    }

    internal class ThickFitResult
    {
        public bool Ok = true;
        public string Error = "";
        public List<Point2d[]> Segments = new List<Point2d[]>();
        public List<string> Log = new List<string>();
        public List<ProblemMark> ProblemPts = new List<ProblemMark>();

        public int MovedNodes;        // узлов подтянуто на контур зоны
        public int KeptFixed;         // узлов не тронуто: они принадлежат пилону/отверстию/контуру
        public int AddedEdges;        // рёбер добавлено по контурам зон
        public int SplitCrossings;    // Х-пересечений, в которые врезан узел
        public int SplitAtNodes;      // рёбер разрезано узлом, лежавшим внутри них
        public int WeldedEdges;       // рёбер схлопнулось при подтяжке (длина < 1 мм)
        public int ShortEdges;        // рёбер короче MinElementSize после подтяжки
        public int CrossingsLeft;     // пересечений без узла, оставшихся после правки
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

        // Ближайшая точка отрезка к точке p (проекция, зажатая концами отрезка).
        private static Point2d ClosestPointOnSegment(Point2d p, Point2d a, Point2d b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < MeshTol.ZeroSq) return a;
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;
            return new Point2d(a.X + dx * t, a.Y + dy * t);
        }

        // Ближайшая точка границы полигона и расстояние до неё.
        private static Point2d ClosestPointOnPolygon(Point2d p, List<Point2d> poly, out double dist)
        {
            dist = double.MaxValue;
            Point2d best = p;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                Point2d c = ClosestPointOnSegment(p, poly[i], poly[(i + 1) % n]);
                double d = c.GetDistanceTo(p);
                if (d < dist) { dist = d; best = c; }
            }
            return best;
        }

        // ПОДТЯЖКА ГОТОВОЙ СЕТКИ К КОНТУРАМ УЧАСТКОВ.
        //
        // Смысл в том, чтобы граница зоны прошла РОВНО ПО РЁБРАМ элементов. Иначе
        // элемент оказывается наполовину в зоне, и его толщину решает положение
        // центра — ошибка до полшага сетки вдоль всей границы.
        //
        // Порядок этапов:
        //   1) узел сетки ближе допуска к контуру — переносится на контур
        //      (вершина контура притягивает в первую очередь: углы зоны обязаны
        //      стать узлами, иначе у угла остаётся срезанный треугольник);
        //   2) рёбра самого контура добавляются в сетку;
        //   3) всё, что пересеклось без узла, режется (жёсткое правило 6), и каждый
        //      узел, попавший внутрь чужого ребра, это ребро делит.
        //
        // Трогается не весь план, а только полоса вокруг зон (габарит зоны плюс
        // допуск): на остальной сетке команда не меняет ни одной координаты, и
        // ручные правки инженера остаются как были.
        internal ThickFitResult FitMeshToThickZones(ThickFitInput input)
        {
            var res = new ThickFitResult();

            if (input.Zones.Count == 0)
            {
                res.Ok = false;
                res.Error = "\nНи одного контура участка другой толщины — нечего подтягивать.\n";
                return res;
            }
            if (input.Segments.Count == 0)
            {
                res.Ok = false;
                res.Error = "\nВ слое " + TriangulationLayerName + " нет линий сетки. Сначала постройте сетку (LIRBUILD).\n";
                return res;
            }

            double tol = input.Tolerance;

            // Жёсткое правило: участок не выходит за контур плиты. Та же
            // проверка и тот же отказ, что у отверстий, — иначе дальше мы добавим
            // в сетку рёбра снаружи плиты и нарушим правило 1.
            if (input.Contour != null && input.Contour.Count >= 3)
            {
                var outside = new List<Point2d>();
                foreach (var z in input.Zones)
                    if (!IsPolygonInsideContour(z.Poly, input.Contour))
                        outside.Add(PolygonCentroid(z.Poly));

                if (outside.Count > 0)
                {
                    res.Ok = false;
                    res.ProblemPts.AddRange(ProblemMark.From(outside, "участок вне плиты"));
                    res.Error = $"\nОшибка: контуров участков вне контура плиты: {outside.Count}. Участок обязан целиком лежать в пределах плиты. Команда остановлена, сетка не тронута. Проблемные места отмечены кругами в слое {ProblemLayerName}.\n";
                    return res;
                }
            }
            else
            {
                res.Log.Add($"\nВНИМАНИЕ: контур плиты (слой {SlabLayerPrefix}H-...)) не найден — проверка «участок другой толщины внутри плиты» пропущена.\n");
            }

            // ---- 1. АКТИВНАЯ ПОЛОСА --------------------------------------------
            // Отрезок попадает в работу, только если его габарит задевает габарит
            // зоны, раздутый на допуск. Узел, который мы вправе подтянуть, лежит не
            // дальше tol от границы зоны, значит он заведомо внутри раздутого
            // габарита — вместе с каждым отрезком, которому он принадлежит.
            // Остальная сетка проходит мимо нетронутой: так ручные правки инженера
            // в других местах плана гарантированно переживают команду.
            var zoneBoxes = new List<double[]>();
            foreach (var z in input.Zones)
            {
                var bb = PolygonBBox(z.Poly);
                zoneBoxes.Add(new double[] { bb[0] - tol - 1.0, bb[1] - tol - 1.0, bb[2] + tol + 1.0, bb[3] + tol + 1.0 });
            }

            bool NearZones(Point2d a, Point2d b)
            {
                double sx0 = Math.Min(a.X, b.X), sy0 = Math.Min(a.Y, b.Y);
                double sx1 = Math.Max(a.X, b.X), sy1 = Math.Max(a.Y, b.Y);
                foreach (var zb in zoneBoxes)
                    if (sx1 >= zb[0] && sx0 <= zb[2] && sy1 >= zb[1] && sy0 <= zb[3]) return true;
                return false;
            }

            var passive = new List<Point2d[]>();
            var active = new List<Point2d[]>();
            foreach (var s in input.Segments)
            {
                if (NearZones(s[0], s[1])) active.Add(s);
                else passive.Add(s);
            }

            if (active.Count == 0)
            {
                res.Ok = false;
                res.Error = "\nРядом с контурами участков нет ни одной линии сетки. Проверьте, что контур нарисован на плите и сетка построена.\n";
                return res;
            }

            // Шаг сетки в работе не задан ничем: сетка уже построена и, возможно,
            // поправлена руками. Берём медиану длин рёбер — на регулярной сетке это
            // и есть шаг. Допуск больше половины шага опасен: узел перепрыгнет
            // соседнюю линию, и ячейка вывернется наизнанку.
            var lens = new List<double>();
            foreach (var s in active) lens.Add(s[0].GetDistanceTo(s[1]));
            lens.Sort();
            double stepGuess = lens[lens.Count / 2];
            if (tol > 0.5 * stepGuess)
                res.Log.Add($"\nВНИМАНИЕ: допуск подтяжки {tol:0.#} мм больше половины шага сетки (шаг ≈ {stepGuess:0.#} мм). Узел может перескочить соседнюю линию и вывернуть ячейку. Разумный допуск — до {0.5 * stepGuess:0.#} мм.\n");

            // ---- 2. ПОДТЯЖКА УЗЛОВ ---------------------------------------------
            var ni = new NodeIndex();
            var nodes = ni.Nodes;
            var segNodes = new List<int[]>();
            foreach (var s in active)
                segNodes.Add(new int[] { ni.GetNode(s[0]), ni.GetNode(s[1]) });

            // Неподвижная геометрия: узел, сидящий на контуре пилона, кромке
            // отверстия или на контуре плиты, остаётся на месте. Пилон от появления
            // участка другой толщины не должен измениться ничем — ни отпечатком, ни узлами.
            bool IsFixedNode(Point2d p)
            {
                foreach (var poly in input.FixedPolys)
                    if (IsOnPolygonBoundary(p, poly, MeshTol.Collinear)) return true;
                return input.Contour != null && IsOnPolygonBoundary(p, input.Contour, MeshTol.Collinear);
            }

            var target = new Point2d[nodes.Count];
            var moved = new bool[nodes.Count];

            for (int i = 0; i < nodes.Count; i++)
            {
                Point2d p = nodes[i];

                // Ближайшая точка границы любой зоны. Вершина контура имеет
                // приоритет: угол зоны обязан стать узлом сетки, иначе у угла
                // останется треугольный огрызок, который считать нечем.
                double best = double.MaxValue;
                Point2d bestPt = p;
                bool bestIsVertex = false;

                foreach (var z in input.Zones)
                {
                    foreach (var v in z.Poly)
                    {
                        double dv = v.GetDistanceTo(p);
                        if (dv <= tol && (!bestIsVertex || dv < best))
                        {
                            best = dv; bestPt = v; bestIsVertex = true;
                        }
                    }
                    if (bestIsVertex) continue;

                    double de;
                    Point2d ce = ClosestPointOnPolygon(p, z.Poly, out de);
                    if (de < best) { best = de; bestPt = ce; }
                }

                if (best > tol) continue;                 // далеко от зоны — не наше дело
                if (best < MeshTol.NodeMerge) continue;   // уже лежит на контуре

                if (IsFixedNode(p)) { res.KeptFixed++; continue; }

                target[i] = bestPt;
                moved[i] = true;
                res.MovedNodes++;
            }

            // ---- 3. ПРИМЕНЕНИЕ СДВИГА ------------------------------------------
            var work = new List<Point2d[]>();
            for (int i = 0; i < active.Count; i++)
            {
                Point2d a = moved[segNodes[i][0]] ? target[segNodes[i][0]] : nodes[segNodes[i][0]];
                Point2d b = moved[segNodes[i][1]] ? target[segNodes[i][1]] : nodes[segNodes[i][1]];

                double len = a.GetDistanceTo(b);
                if (len < MeshTol.MinPiece) { res.WeldedEdges++; continue; }  // оба конца уехали в одну точку
                work.Add(new Point2d[] { a, b });
            }

            // ---- 4. РЁБРА САМОГО КОНТУРА ---------------------------------------
            // Граница зоны обязана быть ребром сетки. Куски, совпавшие с уже
            // существующим ребром, схлопнет разрез по узлам (SplitSegmentsAtNodes
            // выпускает каждое ребро один раз), поэтому дубликатов не будет.
            int zoneEdges = 0;
            foreach (var z in input.Zones)
            {
                int n = z.Poly.Count;
                for (int i = 0; i < n; i++)
                {
                    Point2d a = z.Poly[i], b = z.Poly[(i + 1) % n];
                    if (a.GetDistanceTo(b) < MeshTol.MinPiece) continue;
                    work.Add(new Point2d[] { a, b });
                    zoneEdges++;
                }
            }

            // ---- 5. ВРЕЗКА УЗЛОВ -----------------------------------------------
            // Сначала Х-пересечения (жёсткое правило 6), потом узлы внутри рёбер:
            // ребро контура, пересёкшее линию сетки, и линия сетки, упёршаяся в
            // контур концом, — это два разных случая, и закрывают их разные функции.
            int crossings;
            work = SplitSegmentsAtIntersections(work, out crossings);
            res.SplitCrossings = crossings;

            int splitCount, dropped;
            work = SplitSegmentsAtNodes(work, Math.Max(stepGuess, 1.0), out splitCount, out dropped);
            res.SplitAtNodes = splitCount;

            // Контрольный проход: после разреза пересечений без узла остаться не должно.
            int left;
            var checkSplit = SplitSegmentsAtIntersections(work, out left);
            res.CrossingsLeft = left;
            if (left > 0) work = checkSplit;

            // Рёбра короче минимального элемента — не отказ, но инженеру о них
            // стоит знать: обычно это признак, что допуск подтяжки мал и контур
            // лёг рядом с линией сетки, а не на неё.
            foreach (var s in work)
                if (s[0].GetDistanceTo(s[1]) < MeshTol.MinElementSize) res.ShortEdges++;

            res.AddedEdges = work.Count - (active.Count - res.WeldedEdges);

            res.Segments = new List<Point2d[]>(passive.Count + work.Count);
            res.Segments.AddRange(passive);
            res.Segments.AddRange(work);

            res.Log.Add($"\nУчастков плиты другой толщины: {input.Zones.Count}, рёбер контура: {zoneEdges}; допуск подтяжки: {tol:0.#} мм\n");
            res.Log.Add($"Линий сетки в работе (полоса вокруг участков): {active.Count} из {input.Segments.Count}; после правки их {work.Count}\n");
            res.Log.Add($"Узлов подтянуто на контур: {res.MovedNodes}" +
                (res.KeptFixed > 0 ? $"; оставлено на месте (узлы пилонов, отверстий, контура плиты): {res.KeptFixed}" : "") + "\n");
            res.Log.Add($"Врезано узлов в пересечения: {res.SplitCrossings}; рёбер разрезано узлом: {res.SplitAtNodes}" +
                (res.WeldedEdges > 0 ? $"; схлопнулось рёбер при подтяжке: {res.WeldedEdges}" : "") + "\n");
            if (res.ShortEdges > 0)
                res.Log.Add($"ВНИМАНИЕ: рёбер короче {MeshTol.MinElementSize:0} мм: {res.ShortEdges} — у границы участка остались узкие элементы. Обычно помогает больший допуск подтяжки.\n");
            if (res.CrossingsLeft > 0)
                res.Log.Add($"ВНИМАНИЕ: пересечений линий без узла осталось: {res.CrossingsLeft} (жёсткое правило 6).\n");

            return res;
        }

        // КОМАНДА: участок плиты другой толщины.
        //
        // Контур рисует инженер, команда его классифицирует (уводит на слой
        // MESH_THICK(H-...)) и подтягивает к нему ГОТОВУЮ сетку. Построение заново
        // не запускается: сетка к этому моменту обычно уже поправлена руками.
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

            // Допуск подтяжки: узел сетки ближе этого расстояния к контуру садится
            // на контур, дальше — контур режет ячейку и даёт новые узлы. 120 мм —
            // это 40% обычного шага 300: больше начинает заметно перекашивать
            // соседние ячейки, меньше — чаще режет вместо подтяжки.
            PromptDoubleOptions pdoTol = new PromptDoubleOptions(
                "\nДопуск подтяжки линий сетки к контуру (≈40% шага сетки), мм: ");
            pdoTol.DefaultValue = 120.0;
            pdoTol.AllowNegative = false;
            pdoTol.AllowZero = false;
            PromptDoubleResult pdrTol = ed.GetDouble(pdoTol);
            if (pdrTol.Status != PromptStatus.OK) return;
            double tolerance = pdrTol.Value;

            // Точка возврата: команда правит ГОТОВУЮ сетку, в которой обычно уже есть
            // ручные исправления инженера. Откатить их отменой (U) после закрытия
            // чертежа нельзя, поэтому состояние "до" сохраняется на диск — так же,
            // как это делает LIRBUILD.
            SaveDrawingBeforeWork(doc, ed);

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EraseMarksOnLayer(tr, db, ProblemLayerName);

                string zoneLayer = ThickLayerPrefix + $"H-{thickness:0.###})";
                EnsureLayer(db, tr, zoneLayer, ThickLayerColor);

                int taken = 0, skippedOpen = 0, skippedArcs = 0, skippedService = 0;
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

                    pl.Layer = zoneLayer;
                    taken++;
                }

                ed.WriteMessage($"\nКонтуров принято на слой {zoneLayer}: {taken}" +
                    (skippedOpen > 0 ? $", пропущено незамкнутых: {skippedOpen}" : "") +
                    (skippedArcs > 0 ? $", пропущено с дугами (ЛИРА дуги не принимает): {skippedArcs}" : "") +
                    (skippedService > 0 ? $", пропущено служебных контуров (отверстия/пилоны/плита): {skippedService}" : "") + "\n");

                if (taken == 0)
                {
                    ed.WriteMessage("\nНи одного пригодного контура — сетка не тронута.\n");
                    tr.Commit();
                    return;
                }

                // ---- ЧТЕНИЕ ЧЕРТЕЖА ------------------------------------------------
                int zOpen, zNoThk;
                var zones = GetThickZones(tr, db, out zOpen, out zNoThk);
                if (zNoThk > 0 || zOpen > 0)
                    ed.WriteMessage($"\nВНИМАНИЕ: контуров на слоях {ThickLayerPrefix}...) пропущено: без толщины в имени слоя {zNoThk}, незамкнутых {zOpen}.\n");

                var meshEnts = new List<ObjectId>();
                var meshSegs = new List<Point2d[]>();
                var meshOwner = new List<int>();
                ReadMeshSegments(tr, db, meshEnts, meshSegs, meshOwner);
                ed.WriteMessage($"Линий сетки прочитано из {TriangulationLayerName}: {meshSegs.Count} (объектов: {meshEnts.Count})\n");

                // Неподвижное: контуры пилонов и отверстий. Пилон обязан остаться
                // ровно таким, каким был, — другая толщина плиты его не касается.
                var fixedPolys = new List<List<Point2d>>();
                fixedPolys.AddRange(GetPylonOutlines(tr, db, out _, out _));
                fixedPolys.AddRange(GetHolePolygons(tr, db));

                var contour = GetSlabContour(tr, db);

                var fitInput = new ThickFitInput
                {
                    Segments = meshSegs,
                    Zones = zones,
                    Tolerance = tolerance,
                    FixedPolys = fixedPolys,
                    Contour = contour
                };

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var fit = FitMeshToThickZones(fitInput);
                watch.Stop();
                foreach (var line in fit.Log) ed.WriteMessage(line);

                if (!fit.Ok)
                {
                    // Отказ коммитится: до этой строки команда меняла только слой
                    // контуров, а круги ПРОБЛЕМА без коммита откатились бы вместе
                    // с объяснением, зачем их рисовали.
                    DrawProblemMarks(tr, db, fit.ProblemPts);
                    ed.WriteMessage(fit.Error);
                    tr.Commit();
                    return;
                }

                int erased, added, kept;
                ApplyMeshSegments(tr, db, meshEnts, meshSegs, meshOwner, fit.Segments, out erased, out added, out kept);

                DrawProblemMarks(tr, db, fit.ProblemPts);

                ed.WriteMessage($"Чертёж: оставлено без изменений отрезков {kept}, перерисовано {added}, удалено объектов {erased}\n");
                ed.WriteMessage($"Расчёт подтяжки: {watch.Elapsed.TotalSeconds:0.0} с\n");
                ed.WriteMessage($"Готово. Экспорт (LIREXPORT) даст элементам внутри участка отдельную жёсткость с толщиной из имени слоя; пилоны внутри участка останутся прежними.\n");
                ed.WriteMessage($"ВНИМАНИЕ: повторный LIRBUILD строит сетку заново и эту правку (вместе с ручными) потеряет — после него LIRTHICK нужно повторить.\n");

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
