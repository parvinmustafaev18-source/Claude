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
        // Экспорт готовой сетки в текстовый файл задачи ЛИРА-САПР (входной язык
        // процессора): при импорте плана DXF ЛИРА триангулирует плиту сама и игнорирует
        // наши линии, а текстовый файл задачи (*.txt) она принимает узел в узел.
        // Формат снят с файла, сгенерированного ЛИРОЙ 2024 командой "Создать текстовый
        // файл": документы ( 0/ заголовок ) ( 1/ элементы ) ( 3/ жёсткости ) ( 4/ узлы ).
        [CommandMethod("MESHEXPORTTXT")]
        public void ExportTaskTextCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHEXPORTTXT");
            Database db = doc.Database;

            PromptEntityOptions peo = new PromptEntityOptions("\nВыберите контур плиты (полилинию): ");
            peo.SetRejectMessage("\nМожно выбрать только полилинию (LWPOLYLINE).");
            peo.AddAllowedClass(typeof(Polyline), false);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            // Толщина плиты берётся из имени слоя контура FOUNDATION_SLABS(H-...),
            // проставленного командой MESHLAYERS; ручной запрос — только если контур
            // лежит в другом слое.
            double thicknessMm = 0;
            using (Transaction trLayer = db.TransactionManager.StartTransaction())
            {
                Entity slabEnt = trLayer.GetObject(per.ObjectId, OpenMode.ForRead) as Entity;
                var mH = SlabThicknessRegex.Match(slabEnt != null ? slabEnt.Layer : "");
                if (mH.Success)
                    thicknessMm = ParseLayerNumber(mH.Groups[1].Value);
            }

            if (thicknessMm > 0)
            {
                ed.WriteMessage($"\nТолщина плиты из слоя контура: {thicknessMm:0.###} мм\n");
            }
            else
            {
                PromptDoubleOptions pdoH = new PromptDoubleOptions(
                    "\nВ слое контура нет толщины (нужен FOUNDATION_SLABS(H-...), см. MESHLAYERS). Толщина плиты, мм: ");
                pdoH.DefaultValue = 300.0;
                pdoH.AllowNegative = false;
                pdoH.AllowZero = false;
                PromptDoubleResult pdrH = ed.GetDouble(pdoH);
                if (pdrH.Status != PromptStatus.OK) return;
                thicknessMm = pdrH.Value;
            }
            double thicknessM = thicknessMm / 1000.0;

            // Тело пилона: элементы плиты внутри контура MESH_PYLONS получают ОТДЕЛЬНУЮ
            // жёсткость, чтобы их можно было сделать жёсткой вставкой. Множитель
            // спрашивается только когда такие контуры в чертеже есть, и по умолчанию
            // равен 1 — тогда параметры совпадают с плитой, отличается только номер
            // (ЛИРА принимает одинаковые жёсткости под разными номерами).
            int pylonRectCount = 0;
            using (Transaction trPeek = db.TransactionManager.StartTransaction())
            {
                pylonRectCount = GetPylonOutlines(trPeek, db, out _, out _).Count;
            }

            double pylonStiffFactor = 1.0;
            if (pylonRectCount > 0)
            {
                PromptDoubleOptions pdoPk = new PromptDoubleOptions(
                    $"\nКонтуров пилонов: {pylonRectCount}. Множитель жёсткости тела пилона (E тела = E × k; 1 = как у плиты): ");
                pdoPk.DefaultValue = 1.0;
                pdoPk.AllowNegative = false;
                pdoPk.AllowZero = false;
                PromptDoubleResult pdrPk = ed.GetDouble(pdoPk);
                if (pdrPk.Status != PromptStatus.OK) return;
                pylonStiffFactor = pdrPk.Value;
            }

            // Модуль упругости — выбором класса бетона (начальный модуль Eb по
            // СП 63.13330 в пересчёте на т/м²); Manual — ввод числа напрямую.
            double elasticModulus;
            PromptKeywordOptions pkoE = new PromptKeywordOptions("\nКласс бетона (Manual — ввести E вручную)");
            pkoE.Keywords.Add("B25");
            pkoE.Keywords.Add("B30");
            pkoE.Keywords.Add("B35");
            pkoE.Keywords.Add("B40");
            pkoE.Keywords.Add("Manual");
            pkoE.Keywords.Default = "B30";
            PromptResult prE = ed.GetKeywords(pkoE);
            string concreteClass = prE.Status == PromptStatus.OK ? prE.StringResult
                : prE.Status == PromptStatus.None ? "B30" : null;
            if (concreteClass == null) return;

            switch (concreteClass)
            {
                case "B25": elasticModulus = 3.06e6; break;
                case "B30": elasticModulus = 3.31e6; break;
                case "B35": elasticModulus = 3.52e6; break;
                case "B40": elasticModulus = 3.67e6; break;
                default:
                    PromptDoubleOptions pdoE = new PromptDoubleOptions("\nМодуль упругости E, т/м²: ");
                    pdoE.DefaultValue = 3.31e6;
                    pdoE.AllowNegative = false;
                    pdoE.AllowZero = false;
                    PromptDoubleResult pdrE = ed.GetDouble(pdoE);
                    if (pdrE.Status != PromptStatus.OK) return;
                    elasticModulus = pdrE.Value;
                    break;
            }
            if (concreteClass != "Manual")
                ed.WriteMessage($"\nБетон {concreteClass}: E = {elasticModulus:0.###e+0} т/м²\n");

            // Объёмный вес бетона (R0) — одно значение на всю задачу,
            // пишется в каждую жёсткость документа 3.
            PromptDoubleOptions pdoRo = new PromptDoubleOptions("\nОбъёмный вес бетона R0, т/м³: ");
            pdoRo.DefaultValue = 2.5;
            pdoRo.AllowNegative = false;
            pdoRo.AllowZero = false;
            PromptDoubleResult pdrRo = ed.GetDouble(pdoRo);
            if (pdrRo.Status != PromptStatus.OK) return;
            double unitWeight = pdrRo.Value;

            PromptDoubleOptions pdoFH = new PromptDoubleOptions("\nВысота этажа (стен и пилонов вверх от плиты), мм: ");
            pdoFH.DefaultValue = 3000.0;
            pdoFH.AllowNegative = false;
            pdoFH.AllowZero = false;
            PromptDoubleResult pdrFH = ed.GetDouble(pdoFH);
            if (pdrFH.Status != PromptStatus.OK) return;
            double floorHeight = pdrFH.Value;

            PromptDoubleOptions pdoSH = new PromptDoubleOptions("\nШаг разбивки стен по высоте, мм: ");
            pdoSH.DefaultValue = 300.0;
            pdoSH.AllowNegative = false;
            pdoSH.AllowZero = false;
            PromptDoubleResult pdrSH = ed.GetDouble(pdoSH);
            if (pdrSH.Status != PromptStatus.OK) return;
            double wallStep = pdrSH.Value;

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Polyline pline = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Polyline;
                if (pline == null)
                {
                    ed.WriteMessage("\nНужен замкнутый контур (полилиния).\n");
                    return;
                }
                // Старые маркеры проблем стираются при каждом запуске, чтобы не копились
                EraseMarksOnLayer(tr, db, ProblemLayerName);

                // Дуги и полилинии вне плоскости XY читаются как ломаные в WCS.
                WarnBadPolylines(tr, db, ed);

                // При отказе валидации транзакция коммитится: до этого места команда
                // ничего не меняла, а маркеры разрывов/углов без коммита откатились бы.
                if (!ValidateContour(pline, ed, tr, db, out var contourPts)) { tr.Commit(); return; }
                EnsureCcw(contourPts);

                // Отрезки: контур плиты + линии сетки + стены. Центры пилонов запоминаем,
                // чтобы не превратить внутренность пилона в пластину.
                var segments = new List<Point2d[]>();
                int cn = contourPts.Count;
                for (int i = 0; i < cn; i++)
                    segments.Add(new Point2d[] { contourPts[i], contourPts[(i + 1) % cn] });

                double ParseNum(string s)
                {
                    return double.Parse(s.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
                }

                // Пилоны: центр + размеры сечения из имени слоя COLUMNS(SEC-RC_RECT B-.. H-..)
                var columnCenters = new List<Point2d>();
                var columnDims = new List<double[]>();
                int columnsWithoutDims = 0;

                // Отверстия (проёмы): контуры со слоя MESH_HOLES. Их стороны попадают в
                // планарный граф (элементы плиты смыкаются на кромке отверстия), а сама
                // грань отверстия ниже исключается из заливки элементами.
                var holePolys = new List<List<Point2d>>();
                int holeEntCount = 0;      // всего объектов на слое MESH_HOLES (любых)
                int holeOpenPolyCount = 0; // из них незамкнутых полилиний

                // Дверные проёмы в стенах: отрезки на слое WALL_DOORS(H-<высота>),
                // нарисованные поверх оси стены на длину проёма. В экспорте кусок стены
                // под таким отрезком не выдавливается снизу до высоты двери — остаётся
                // только перемычка выше проёма.
                var doorOrig = new List<Point2d[]>();
                var doorHeights = new List<double>();

                // Стены: исходные отрезки + толщина из имени слоя WALLS(H-..).
                // Дополнительно помечаем, является ли отрезок осью пилона (суффикс PILON),
                // и ключ типоразмера пилона (толщина x длина) — по нему пилону выдаётся
                // отдельный номер жёсткости, чтобы он не слился со стеной той же толщины.
                var wallOrig = new List<Point2d[]>();
                var wallOrigThickness = new List<double>();
                var wallOrigIsPylon = new List<bool>();
                var wallOrigSizeKey = new List<string>();

                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || string.IsNullOrEmpty(ent.Layer)) continue;

                    if (ent is DBPoint dbp && IsColumnLayer(ent.Layer))
                    {
                        columnCenters.Add(new Point2d(dbp.Position.X, dbp.Position.Y));
                        var m = ColumnDimsRegex.Match(ent.Layer);
                        if (m.Success)
                        {
                            columnDims.Add(new double[] { ParseNum(m.Groups[1].Value), ParseNum(m.Groups[2].Value) });
                        }
                        else
                        {
                            columnDims.Add(new double[] { 400.0, 400.0 });
                            columnsWithoutDims++;
                        }
                        continue;
                    }

                    if (ent.Layer == HoleLayerName)
                    {
                        holeEntCount++;
                        if (ent is Polyline hpl)
                        {
                            if (!hpl.Closed) holeOpenPolyCount++;
                            else
                            {
                                var hv = GetPolylineVertices(hpl);
                                if (hv.Count >= 3)
                                {
                                    EnsureCcw(hv);
                                    holePolys.Add(hv);
                                    int hc = hv.Count;
                                    for (int i = 0; i < hc; i++)
                                        segments.Add(new Point2d[] { hv[i], hv[(i + 1) % hc] });
                                }
                            }
                        }
                        continue;
                    }

                    if (IsDoorLayer(ent.Layer))
                    {
                        // Высота из имени слоя; слой без "H-" — дверь стандартной высоты.
                        double dh;
                        if (!TryParseLayerHeight(ent.Layer, out dh)) dh = 2100.0;
                        if (ent is Line dln)
                        {
                            doorOrig.Add(new Point2d[] {
                                new Point2d(dln.StartPoint.X, dln.StartPoint.Y),
                                new Point2d(dln.EndPoint.X, dln.EndPoint.Y) });
                            doorHeights.Add(dh);
                        }
                        else if (ent is Polyline dpl)
                        {
                            var dv = GetPolylineVertices(dpl);
                            int dc = dpl.Closed ? dv.Count : dv.Count - 1;
                            for (int i = 0; i < dc; i++)
                            {
                                doorOrig.Add(new Point2d[] { dv[i], dv[(i + 1) % dv.Count] });
                                doorHeights.Add(dh);
                            }
                        }
                        continue;
                    }

                    bool isWall = IsWallLayer(ent.Layer);
                    bool isPylon = IsPylonLayer(ent.Layer);
                    bool meshLayer = ent.Layer == TriangulationLayerName || isWall;
                    if (!meshLayer) continue;

                    double wallT = 200.0;
                    if (isWall) TryParseLayerHeight(ent.Layer, out wallT);

                    // Типоразмер пилона = толщина (короткая сторона) x длина оси (длинная).
                    string PylonKey(Point2d[] s) =>
                        Math.Round(wallT, 1) + "x" + Math.Round(s[0].GetDistanceTo(s[1]), 0);

                    if (ent is Line line)
                    {
                        var seg = new Point2d[]
                        {
                            new Point2d(line.StartPoint.X, line.StartPoint.Y),
                            new Point2d(line.EndPoint.X, line.EndPoint.Y)
                        };
                        segments.Add(seg);
                        if (isWall)
                        {
                            wallOrig.Add(seg); wallOrigThickness.Add(wallT);
                            wallOrigIsPylon.Add(isPylon);
                            wallOrigSizeKey.Add(isPylon ? PylonKey(seg) : null);
                        }
                    }
                    else if (ent is Polyline wp)
                    {
                        var verts = GetPolylineVertices(wp);
                        int segCount = wp.Closed ? verts.Count : verts.Count - 1;
                        for (int i = 0; i < segCount; i++)
                        {
                            var seg = new Point2d[] { verts[i], verts[(i + 1) % verts.Count] };
                            segments.Add(seg);
                            if (isWall)
                            {
                                wallOrig.Add(seg); wallOrigThickness.Add(wallT);
                                wallOrigIsPylon.Add(isPylon);
                                wallOrigSizeKey.Add(isPylon ? PylonKey(seg) : null);
                            }
                        }
                    }
                }

                ed.WriteMessage($"\n[диагностика отверстий] объектов на слое {HoleLayerName}: {holeEntCount}; из них замкнутых контуров принято: {holePolys.Count}, незамкнутых полилиний: {holeOpenPolyCount}\n");

                // Контуры тел пилонов. В планарный граф они НЕ добавляются: их грани уже
                // лежат в сетке (MESHQUADMESH отпечатывает контур), а лишние рёбра дали
                // бы наложение. Нужны только для того, чтобы отличить элементы плиты,
                // попавшие в тело пилона, и дать им свою жёсткость.
                var pylonRects = GetPylonOutlines(tr, db, out _, out _);

                segments = DeduplicateSegments(segments);

                // Косяки дверных проёмов: стену режем ровно в концах дверного отрезка.
                // Делать это в MESHQUADMESH бесполезно — вдоль оси стены линий сетки нет
                // (ResolveOverlappingSegments снимает их как перекрытые стеной), резать
                // там нечего. Здесь стена присутствует в графе как обычный отрезок, и
                // после разрезки кусок стены точно совпадает с проёмом.
                int doorJambSplits = 0;
                if (doorOrig.Count > 0)
                {
                    var jambs = new List<Point2d>();
                    foreach (var d in doorOrig) { jambs.Add(d[0]); jambs.Add(d[1]); }
                    segments = SplitSegmentsAtPoints(segments, jambs, MeshTol.DoorOnAxis, out doorJambSplits);
                }

                segments = SplitSegmentsAtNodes(segments, 500.0, out _);
                segments = DeduplicateSegments(segments);

                // Узлы и рёбра планарного графа
                var ni = new NodeIndex();
                var nodes = ni.Nodes;

                var edges = new List<int[]>();
                foreach (var seg in segments)
                {
                    int ia = ni.GetNode(seg[0]);
                    int ib = ni.GetNode(seg[1]);
                    if (ia != ib) edges.Add(new int[] { ia, ib });
                }

                var faces = ExtractPlanarFaces(nodes, edges);

                // Глобальные 3D-узлы задачи (плита z=0, стены и пилоны растут вверх).
                // Узлы сливаются по допуску: низ стены обязан попасть ровно в узел
                // плиты, иначе стена в ЛИРЕ стоит на собственных узлах и
                // «проваливается» сквозь плиту.
                var ni3 = new NodeIndex3();
                var nodes3 = ni3.Nodes;
                int Node3(double x, double y, double z) { return ni3.GetNode(x, y, z); }
                int SlabNode(int i2d) { return Node3(nodes[i2d].X, nodes[i2d].Y, 0.0); }

                // Жёсткости: 1 — плита; далее стены по толщинам; далее сечения пилонов
                // Жёсткости пластин: ключ составной — "W<толщина>" для стены и
                // "P<толщина>x<длина>" для пилона, поэтому пилон получает СВОЙ номер
                // жёсткости даже при толщине, совпадающей со стеной (ЛИРА принимает
                // одинаковые по параметрам жёсткости под разными номерами). Имени или
                // комментария у жёсткости в текстовом формате нет, поэтому расшифровка
                // номеров пишется в командную строку и в файл легенды рядом с задачей.
                var wallStiffIds = new Dictionary<string, int>();
                var wallStiffThk = new Dictionary<int, double>();   // № жёсткости -> толщина, мм
                var wallStiffTitle = new Dictionary<int, string>(); // № жёсткости -> расшифровка
                // Переопределение модуля упругости для отдельных номеров (тело пилона).
                // Для остальных берётся общий elasticModulus.
                var wallStiffE = new Dictionary<int, double>();
                var colStiffIds = new Dictionary<string, int>();
                var colStiffDims = new List<double[]>();
                int nextStiff = 2;

                // Элементы: {тип КЭ, № жёсткости, узлы...}
                var elements = new List<int[]>();

                int failedFaces = 0, fanFaces = 0;
                var lostFaceCenters = new List<string>();
                var lostFacePts = new List<Point2d>();

                // Грани -> пластины плиты: 3 узла -> КЭ 42, 4 узла -> КЭ 44 (порядок узлов
                // КЭ 44 — "змейкой": p0 p1 p3 p2), больше 4 (висячие узлы) -> триангуляция.
                // Грань с центром пилона внутри разбивается веером треугольников вокруг
                // центра — центр становится узлом сетки, к нему цепляется стержень пилона.
                int spikeFans = 0, multiSpikeFaces = 0;
                foreach (var rawFace in faces)
                {
                    // Конец стены внутри ячейки — тупиковое ребро графа: обход грани
                    // проходит по нему туда и обратно, в грани появляется шип
                    // "... B, S, B ...". Такая грань не триангулировалась — под концом
                    // стены оставалась дыра в плите. Шип вырезается, грань разбивается
                    // веером треугольников вокруг конца стены S — узел стены связан
                    // с пластинами плиты.
                    var face = new List<int>(rawFace);
                    var spikeTips = new List<int>();
                    bool spikeRemoved = true;
                    while (spikeRemoved && face.Count >= 3)
                    {
                        spikeRemoved = false;
                        int fm = face.Count;
                        for (int i = 0; i < fm; i++)
                        {
                            if (face[(i - 1 + fm) % fm] != face[(i + 1) % fm]) continue;
                            spikeTips.Add(face[i]);
                            int iNext = (i + 1) % fm;
                            if (iNext > i) { face.RemoveAt(iNext); face.RemoveAt(i); }
                            else { face.RemoveAt(i); face.RemoveAt(iNext); }
                            spikeRemoved = true;
                            break;
                        }
                    }
                    if (face.Count < 3) continue;

                    var poly = new List<Point2d>();
                    foreach (int idx in face) poly.Add(nodes[idx]);

                    if (spikeTips.Count > 0)
                    {
                        if (spikeTips.Count > 1) multiSpikeFaces++;
                        Point2d s = nodes[spikeTips[0]];
                        int sNode = Node3(s.X, s.Y, 0.0);
                        for (int i = 0; i < face.Count; i++)
                        {
                            Point2d va = nodes[face[i]];
                            Point2d vb = nodes[face[(i + 1) % face.Count]];
                            if (Math.Abs(CrossProduct(va, vb, s)) < 1.0) continue; // вырожденный треугольник
                            elements.Add(new int[] { 42, 1, sNode, SlabNode(face[i]), SlabNode(face[(i + 1) % face.Count]) });
                        }
                        spikeFans++;
                        continue;
                    }

                    int colIdx = -1;
                    for (int c = 0; c < columnCenters.Count; c++)
                        if (IsPointInPolygon(columnCenters[c], poly)) { colIdx = c; break; }

                    if (colIdx >= 0)
                    {
                        int cNode = Node3(columnCenters[colIdx].X, columnCenters[colIdx].Y, 0.0);
                        for (int i = 0; i < face.Count; i++)
                            elements.Add(new int[] { 42, 1, cNode, SlabNode(face[i]), SlabNode(face[(i + 1) % face.Count]) });
                        fanFaces++;
                    }
                    else if (face.Count == 3)
                    {
                        elements.Add(new int[] { 42, 1, SlabNode(face[0]), SlabNode(face[1]), SlabNode(face[2]) });
                    }
                    else if (face.Count == 4)
                    {
                        // Прямоугольная ячейка -> КЭ 41 (прямоугольный элемент оболочки),
                        // прочие четырёхугольники -> КЭ 44. Порядок узлов одинаков ("змейкой").
                        bool rect = true;
                        for (int i = 0; i < 4 && rect; i++)
                        {
                            Point2d pp = poly[(i + 3) % 4], pc = poly[i], pn = poly[(i + 1) % 4];
                            double l1 = pc.GetDistanceTo(pp), l2 = pc.GetDistanceTo(pn);
                            if (l1 < 1e-9 || l2 < 1e-9) { rect = false; break; }
                            double dot = ((pp.X - pc.X) * (pn.X - pc.X) + (pp.Y - pc.Y) * (pn.Y - pc.Y)) / (l1 * l2);
                            if (Math.Abs(dot) > 1e-3) rect = false;
                        }
                        elements.Add(new int[] { rect ? 41 : 44, 1, SlabNode(face[0]), SlabNode(face[1]), SlabNode(face[3]), SlabNode(face[2]) });
                    }
                    else
                    {
                        int failed = 0;
                        foreach (var t in TriangulateSimplePolygon(poly, ref failed))
                            elements.Add(new int[] { 42, 1, Node3(t[0].X, t[0].Y, 0), Node3(t[1].X, t[1].Y, 0), Node3(t[2].X, t[2].Y, 0) });
                        failedFaces += failed;
                        if (failed > 0)
                        {
                            Point2d fc = PolygonCentroid(poly);
                            lostFaceCenters.Add($"({fc.X:0}, {fc.Y:0})");
                            lostFacePts.Add(fc);
                        }
                    }
                }

                if (spikeFans > 0)
                    ed.WriteMessage($"\nКонцов стен внутри ячеек, врезанных в плиту веером треугольников: {spikeFans}\n");
                if (multiSpikeFaces > 0)
                    ed.WriteMessage($"\nВНИМАНИЕ: ячеек с несколькими тупиковыми концами стен: {multiSpikeFaces} — связан только первый конец, проверьте сетку у этих стен\n");

                // ОТВЕРСТИЯ (простое и надёжное правило): удаляем готовые элементы плиты,
                // чей ЦЕНТР попал внутрь контура отверстия. Центр отдельного КЭ (тр-к или
                // выпуклый 4-угольник) всегда лежит строго внутри него, поэтому тест не
                // срывается на вогнутых/разрезанных стеной областях. Элемент либо целиком
                // в отверстии (удаляем), либо целиком снаружи (оставляем) — узлы сетки
                // сидят на кромке проёма. Стен это не касается: они добавляются ниже.
                int holeElemsRemoved = 0;
                if (holePolys.Count > 0)
                {
                    var keptElems = new List<int[]>();
                    foreach (var el in elements)
                    {
                        double cx = 0, cy = 0;
                        int vcount = el.Length - 2;
                        for (int k = 2; k < el.Length; k++) { cx += nodes3[el[k]][0]; cy += nodes3[el[k]][1]; }
                        Point2d ec = new Point2d(cx / vcount, cy / vcount);
                        bool inHole = false;
                        foreach (var hp in holePolys)
                            if (IsPointInPolygon(ec, hp)) { inHole = true; break; }
                        if (inHole) { holeElemsRemoved++; continue; }
                        keptElems.Add(el);
                    }
                    elements = keptElems;

                    // Ложные «потерянные грани» внутри отверстия — это и есть дырка, а не
                    // проблема сетки: убираем такие точки, чтобы не рисовать круги в проёме.
                    for (int i = lostFacePts.Count - 1; i >= 0; i--)
                    {
                        bool inHole = false;
                        foreach (var hp in holePolys)
                            if (IsPointInPolygon(lostFacePts[i], hp)) { inHole = true; break; }
                        if (inHole) { lostFacePts.RemoveAt(i); lostFaceCenters.RemoveAt(i); }
                    }
                }

                // ТЕЛО ПИЛОНА — отдельная жёсткость. Признак тот же, что у отверстий:
                // ЦЕНТР готового элемента внутри контура пилона. Центр треугольника или
                // выпуклого четырёхугольника всегда строго внутри него, поэтому тест не
                // срывается ни на какой форме элемента, а узлы сетки сидят ровно на грани
                // пилона (это обеспечивает отпечаток в MESHQUADMESH).
                // Считается ДО добавления стен: в elements сейчас только пластины плиты.
                int pylonBodyElems = 0, pylonBodyStiffId = 0;
                if (pylonRects.Count > 0)
                {
                    foreach (var el in elements)
                    {
                        double cx = 0, cy = 0;
                        int vcount = el.Length - 2;
                        for (int k = 2; k < el.Length; k++) { cx += nodes3[el[k]][0]; cy += nodes3[el[k]][1]; }
                        Point2d ec = new Point2d(cx / vcount, cy / vcount);

                        bool inPylon = false;
                        foreach (var pr in pylonRects)
                            if (IsPointInPolygon(ec, pr)) { inPylon = true; break; }
                        if (!inPylon) continue;

                        if (pylonBodyStiffId == 0)
                        {
                            pylonBodyStiffId = nextStiff++;
                            wallStiffThk[pylonBodyStiffId] = thicknessMm;
                            wallStiffE[pylonBodyStiffId] = elasticModulus * pylonStiffFactor;
                            wallStiffTitle[pylonBodyStiffId] = $"тело пилона (плита H-{thicknessMm:0.#}"
                                + (Math.Abs(pylonStiffFactor - 1.0) > 1e-9 ? $", E×{pylonStiffFactor:0.###}" : ", E как у плиты")
                                + ")";
                        }

                        el[1] = pylonBodyStiffId;
                        pylonBodyElems++;
                    }
                }

                int slabElemCount = elements.Count;
                if (slabElemCount == 0)
                {
                    ed.WriteMessage("\nНе найдено ни одной замкнутой ячейки сетки — сначала постройте сетку (MESHQUADMESH).\n");
                    return;
                }

                // ИНВАРИАНТ БАЛАНСА ПЛОЩАДЕЙ. Считается здесь, пока в elements лежат
                // только пластины плиты (стены и стержни добавляются ниже и площади в
                // плане не дают), а печатается вместе с итогом экспорта.
                double slabArea = 0.0;
                for (int i = 0; i < slabElemCount; i++)
                    slabArea += ElementPlanArea(elements[i], nodes3);
                double holesArea;
                double targetArea = SlabTargetArea(contourPts, holePolys, out holesArea);

                // Стены -> вертикальные оболочки КЭ 44: кусок стены после разрезки узлами
                // сетки выдавливается вверх на высоту этажа. Шаг по высоте держится ровно
                // wallStep, остаток высоты идёт отдельным (последним) рядом: 3500/300 =
                // ряды 300...300 + один 200, а не одиннадцать по 292.
                var zLevels = new List<double> { 0.0 };
                double zCur = wallStep;
                while (zCur < floorHeight - 1e-6)
                {
                    zLevels.Add(zCur);
                    zCur += wallStep;
                }
                zLevels.Add(floorHeight);
                // Высоты дверных проёмов добавляем как отметки рядов, чтобы верх проёма
                // (низ перемычки) лёг точно на doorH при любом wallStep.
                foreach (var dh in doorHeights)
                {
                    if (dh > 1e-6 && dh < floorHeight - 1e-6) zLevels.Add(dh);
                    else if (dh >= floorHeight - 1e-6)
                        ed.WriteMessage($"\nВНИМАНИЕ: высота дверного проёма {dh:0.#} >= высоты этажа {floorHeight:0.#} — стена под таким проёмом не будет выдавлена совсем (перемычки нет).\n");
                }
                zLevels.Sort();
                for (int i = zLevels.Count - 1; i > 0; i--)
                    if (zLevels[i] - zLevels[i - 1] < 1e-6) zLevels.RemoveAt(i);
                int rows = zLevels.Count - 1;
                int wallElemCount = 0;
                int doorPiers = 0;      // кусков стены, попавших под дверь
                int doorRowsSkipped = 0; // рядов КЭ 44, не поставленных из-за проёма

                foreach (var seg in segments)
                {
                    // Ось пилона часто лежит на линии стены (пилон внутри/вдоль стены).
                    // Брать первое совпадение нельзя — пилон получил бы толщину и блок
                    // стены. Поэтому запоминаем первое совпадение, но запись с PILON
                    // всегда перебивает обычную стену.
                    double thickness = -1; int wIdx = -1;
                    for (int w = 0; w < wallOrig.Count; w++)
                    {
                        if (!IsPointOnSegment(seg[0], wallOrig[w][0], wallOrig[w][1], MeshTol.OnSegment) ||
                            !IsPointOnSegment(seg[1], wallOrig[w][0], wallOrig[w][1], MeshTol.OnSegment)) continue;

                        if (wIdx < 0) { wIdx = w; thickness = wallOrigThickness[w]; }
                        if (wallOrigIsPylon[w]) { wIdx = w; thickness = wallOrigThickness[w]; break; }
                    }
                    if (thickness < 0) continue;

                    double tKey = Math.Round(thickness, 1);
                    bool segIsPylon = wallOrigIsPylon[wIdx];
                    string sizeKey = segIsPylon ? (wallOrigSizeKey[wIdx] ?? tKey.ToString()) : null;

                    // Жёсткость: у пилона свой номер по типоразмеру, у стены — по толщине.
                    // Параметры (E, толщина, RO) при этом могут совпадать — так и задумано.
                    string stiffKey = segIsPylon ? ("P" + sizeKey) : ("W" + tKey);
                    int stiffId;
                    if (!wallStiffIds.TryGetValue(stiffKey, out stiffId))
                    {
                        wallStiffIds[stiffKey] = stiffId = nextStiff++;
                        wallStiffThk[stiffId] = tKey;
                        wallStiffTitle[stiffId] = segIsPylon
                            ? $"пилон {sizeKey} (пластина H-{tKey:0.#})"
                            : $"стена H-{tKey:0.#}";
                    }

                    // Дверной проём: если середина куска стены лежит на линии
                    // WALL_DOORS, ряды от пола до высоты двери не ставим (остаётся
                    // перемычка выше). Высота — из имени слоя двери.
                    double doorH = 0;
                    if (doorOrig.Count > 0)
                    {
                        Point2d mid = new Point2d((seg[0].X + seg[1].X) / 2.0, (seg[0].Y + seg[1].Y) / 2.0);
                        for (int d = 0; d < doorOrig.Count; d++)
                            if (IsPointOnSegment(mid, doorOrig[d][0], doorOrig[d][1], MeshTol.DoorOnAxis) && doorHeights[d] > doorH)
                                doorH = doorHeights[d];
                        if (doorH > 0) doorPiers++;
                    }

                    for (int k = 1; k <= rows; k++)
                    {
                        if (doorH > 0 && zLevels[k] <= doorH + 1e-6) { doorRowsSkipped++; continue; }
                        int aLow = Node3(seg[0].X, seg[0].Y, zLevels[k - 1]);
                        int bLow = Node3(seg[1].X, seg[1].Y, zLevels[k - 1]);
                        int aUp = Node3(seg[0].X, seg[0].Y, zLevels[k]);
                        int bUp = Node3(seg[1].X, seg[1].Y, zLevels[k]);
                        elements.Add(new int[] { 44, stiffId, aLow, bLow, aUp, bUp });
                        wallElemCount++;
                    }
                }

                // Пилоны -> вертикальные стержни КЭ 10 от центра (узел веера в плите)
                // до отметки этажа. Сечение — из имени слоя, в жёсткость S0 (см).
                int barCount = 0;
                for (int c = 0; c < columnCenters.Count; c++)
                {
                    string dimKey = columnDims[c][0].ToString("0.#") + "x" + columnDims[c][1].ToString("0.#");
                    if (!colStiffIds.ContainsKey(dimKey))
                    {
                        colStiffIds[dimKey] = nextStiff++;
                        colStiffDims.Add(new double[] { columnDims[c][0], columnDims[c][1], colStiffIds[dimKey] });
                    }
                    int bottom = Node3(columnCenters[c].X, columnCenters[c].Y, 0.0);
                    int top = Node3(columnCenters[c].X, columnCenters[c].Y, floorHeight);
                    elements.Add(new int[] { 10, colStiffIds[dimKey], bottom, top });

                    barCount++;
                }

                // Запись файла (кодировка 1251, числа с точкой, координаты мм -> м).
                // Имя задачи в документе 0 обязано совпадать с именем файла — иначе
                // ЛИРА пишет предупреждение и переименовывает задачу.
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string dwgPath = db.Filename;
                string baseDir = (string.IsNullOrEmpty(dwgPath) || dwgPath.StartsWith("."))
                    ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments)
                    : System.IO.Path.GetDirectoryName(dwgPath);
                string taskName = ((string.IsNullOrEmpty(dwgPath) || dwgPath.StartsWith("."))
                    ? "MESHPLUGIN"
                    : System.IO.Path.GetFileNameWithoutExtension(dwgPath).ToUpperInvariant()) + "_LIRA";
                string planDir = System.IO.Path.Combine(baseDir, "LIRA_PLANS");
                System.IO.Directory.CreateDirectory(planDir);
                string outPath = System.IO.Path.Combine(planDir, taskName + ".txt");

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("( 0/");
                sb.AppendLine("1; " + taskName + "/");
                sb.AppendLine("2; 5/");
                sb.AppendLine("33;M 1 CM 100 T 1 C 1 /");
                sb.AppendLine("39;");
                sb.AppendLine("1: ЗАГРУЖЕНИЕ 1 ;");
                sb.AppendLine(" /");
                sb.AppendLine(")");

                sb.AppendLine("( 1/");
                foreach (var el in elements)
                {
                    sb.Append(el[0]).Append(' ').Append(el[1]);
                    for (int i = 2; i < el.Length; i++)
                        sb.Append(' ').Append(el[i] + 1);
                    sb.AppendLine(" /");
                }
                sb.AppendLine(")");

                sb.AppendLine("( 3/");
                string roStr = "RO " + unitWeight.ToString("0.###", inv);
                sb.AppendLine("1 GEI " + elasticModulus.ToString("0.###e+000", inv) + " 0.2 "
                    + thicknessM.ToString("0.###", inv) + " " + roStr + " /");
                // Жёсткости пластин по возрастанию номера: стены и пилоны идут отдельными
                // номерами, даже если параметры совпадают (различие — только в номере).
                var wallStiffOrdered = new List<int>(wallStiffThk.Keys);
                wallStiffOrdered.Sort();
                foreach (int sid in wallStiffOrdered)
                {
                    double eSid;
                    if (!wallStiffE.TryGetValue(sid, out eSid)) eSid = elasticModulus;
                    sb.AppendLine(sid + " GEI " + eSid.ToString("0.###e+000", inv) + " 0.2 "
                        + (wallStiffThk[sid] / 1000.0).ToString("0.###", inv) + " " + roStr + " /");
                }
                foreach (var cd in colStiffDims)
                {
                    sb.AppendLine((int)cd[2] + " S0 " + elasticModulus.ToString("0.###e+000", inv) + " "
                        + (cd[0] / 10.0).ToString("0.#", inv) + " " + (cd[1] / 10.0).ToString("0.#", inv)
                        + " " + roStr + "/");
                    sb.AppendLine(" 0 Mu 0.2/");
                }
                sb.AppendLine(")");

                sb.AppendLine("( 4/");
                foreach (var p in nodes3)
                {
                    sb.Append((p[0] / 1000.0).ToString("0.#####", inv)).Append(' ');
                    sb.Append((p[1] / 1000.0).ToString("0.#####", inv)).Append(' ');
                    sb.Append((p[2] / 1000.0).ToString("0.#####", inv)).Append(" /");
                    sb.AppendLine();
                }
                sb.AppendLine(")");

                System.IO.File.WriteAllText(outPath, sb.ToString(), System.Text.Encoding.GetEncoding(1251));

                // Легенда: в текстовом формате ЛИРЫ у жёсткости нет ни имени, ни
                // комментария, поэтому расшифровка номеров пишется отдельным файлом
                // рядом с задачей — по нему видно, где стена, а где пилон.
                string legendPath = System.IO.Path.Combine(planDir, taskName + "_LEGEND.txt");
                var lg = new System.Text.StringBuilder();
                lg.AppendLine("Расшифровка номеров для задачи " + taskName);
                lg.AppendLine();
                lg.AppendLine("ЖЁСТКОСТИ (документ 3)");
                lg.AppendLine("  1 = фундаментная плита H-" + thicknessMm.ToString("0.#", inv));
                foreach (int sid in wallStiffOrdered)
                    lg.AppendLine("  " + sid + " = " + wallStiffTitle[sid]);
                foreach (var cd in colStiffDims)
                    lg.AppendLine("  " + (int)cd[2] + " = пилон-стержень "
                        + cd[0].ToString("0.#", inv) + "x" + cd[1].ToString("0.#", inv));
                System.IO.File.WriteAllText(legendPath, lg.ToString(), System.Text.Encoding.GetEncoding(1251));

                int rectCount = 0, quadCount = 0, triCount = 0;
                for (int i = 0; i < slabElemCount; i++)
                    if (elements[i][0] == 41) rectCount++;
                    else if (elements[i][0] == 44) quadCount++;
                    else triCount++;

                ed.WriteMessage($"\nЭкспортировано: узлов {nodes3.Count}; плита: КЭ 41 {rectCount}, КЭ 44 {quadCount}, КЭ 42 {triCount} (вееров под пилонами: {fanFaces}); стены: КЭ 44 {wallElemCount} (толщин: {wallStiffIds.Count}); пилоны: стержней КЭ 10 {barCount} (сечений: {colStiffIds.Count})" +
                    $"; осей пилонов (PILON) в чертеже: {wallOrigIsPylon.FindAll(p => p).Count}" +
                    (holePolys.Count > 0 ? $"; отверстий: {holePolys.Count} (удалено элементов внутри: {holeElemsRemoved})" : "")
                    + (pylonRects.Count > 0 ? $"; тела пилонов: контуров {pylonRects.Count}, элементов плиты в них {pylonBodyElems}" + (pylonBodyStiffId > 0 ? $" (жёсткость №{pylonBodyStiffId})" : "") : "") +
                    (doorOrig.Count > 0 ? $"; дверных проёмов: {doorOrig.Count} (врезано узлов на косяках: {doorJambSplits}, кусков стены под дверью: {doorPiers}, пропущено рядов КЭ 44: {doorRowsSkipped})" : "") +
                    (failedFaces > 0 ? $"; потеряно граней: {failedFaces}" : "") +
                    (columnsWithoutDims > 0 ? $"; пилонов без размеров в имени слоя (принято 400x400): {columnsWithoutDims}" : "") + "\n");
                // Главная проверка результата: покрывают ли пластины плиту целиком.
                ReportAreaBalance(ed, slabArea, targetArea, holesArea, slabElemCount);

                // Раскладка номеров — чтобы сверить с тем, что показала ЛИРА.
                ed.WriteMessage($"Жёсткости: 1 = плита H-{thicknessMm:0.#}\n");
                foreach (int sid in wallStiffOrdered)
                    ed.WriteMessage($"  {sid} = {wallStiffTitle[sid]}\n");
                ed.WriteMessage($"Легенда: {legendPath}\n");

                if (lostFaceCenters.Count > 0)
                {
                    DrawMarkCircles(tr, db, ProblemLayerName, lostFacePts, ProblemMarkRadius);
                    ed.WriteMessage($"\nВНИМАНИЕ: не удалось разбить ячеек: {lostFaceCenters.Count}, центры: {string.Join(", ", lostFaceCenters)} — в этих местах в ЛИРЕ будут дыры. Ячейки отмечены кругами в слое {ProblemLayerName}.\n");
                }
                ed.WriteMessage($"Файл: {outPath}\n");
                ed.WriteMessage("Импорт в ЛИРЕ: Файл → Импортировать задачу → тип \"Текстовые файлы (*.txt)\". После импорта рекомендуется Упаковка схемы.\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHEXPORTTXT: {ex.Message}\n");
            }
        }

        // Обход минимальных граней планарного графа: из каждого направленного ребра
        // идём, выбирая в каждом узле следующее ребро по часовой стрелке от обратного —
        // внутренние грани обходятся против часовой (положительная площадь), внешняя
        // грань — по часовой (отрицательная) и отбрасывается.
        private List<List<int>> ExtractPlanarFaces(
            List<Point2d> nodes,
            List<int[]> edges)
        {
            int n = nodes.Count;
            var neighbors = new List<List<int>>();
            for (int i = 0; i < n; i++)
                neighbors.Add(new List<int>());

            var seenEdge = new HashSet<long>();
            foreach (var e in edges)
            {
                int a = e[0], b = e[1];
                long key = (long)Math.Min(a, b) * n + Math.Max(a, b);
                if (!seenEdge.Add(key)) continue;
                neighbors[a].Add(b);
                neighbors[b].Add(a);
            }

            for (int i = 0; i < n; i++)
            {
                int self = i;
                neighbors[i].Sort((p, q) =>
                {
                    double ap = Math.Atan2(nodes[p].Y - nodes[self].Y, nodes[p].X - nodes[self].X);
                    double aq = Math.Atan2(nodes[q].Y - nodes[self].Y, nodes[q].X - nodes[self].X);
                    return ap.CompareTo(aq);
                });
            }

            var visited = new HashSet<long>();
            var rawFaces = new List<List<int>>();
            var rawAreas = new List<double>();
            int maxSteps = edges.Count * 4 + 8;

            for (int start = 0; start < n; start++)
            {
                foreach (int firstNb in neighbors[start])
                {
                    if (visited.Contains((long)start * n + firstNb)) continue;

                    var face = new List<int> { start };
                    int a = start, b = firstNb;
                    int steps = 0;
                    bool closed = false;

                    while (steps++ < maxSteps)
                    {
                        visited.Add((long)a * n + b);
                        face.Add(b);

                        var nb = neighbors[b];
                        int idx = nb.IndexOf(a);
                        int next = nb[(idx - 1 + nb.Count) % nb.Count];
                        a = b;
                        b = next;

                        if (a == start && b == firstNb) { closed = true; break; }
                    }

                    if (!closed || face.Count < 4) continue; // face содержит стартовый узел дважды

                    face.RemoveAt(face.Count - 1); // последний равен первому
                    var poly = new List<Point2d>();
                    foreach (int idx in face) poly.Add(nodes[idx]);

                    double area = PolygonArea(poly);
                    if (Math.Abs(area) < 1e-3) continue;

                    rawFaces.Add(face);
                    rawAreas.Add(area);
                }
            }

            // Внутренние грани — с положительной площадью; если ориентация обхода
            // оказалась противоположной (внутренних больше среди отрицательных),
            // берём отрицательные и разворачиваем.
            int pos = 0, neg = 0;
            for (int i = 0; i < rawAreas.Count; i++)
                if (rawAreas[i] > 0) pos++; else neg++;
            bool takePositive = pos >= neg;

            var result = new List<List<int>>();
            for (int i = 0; i < rawFaces.Count; i++)
            {
                if (takePositive && rawAreas[i] > 0)
                {
                    result.Add(rawFaces[i]);
                }
                else if (!takePositive && rawAreas[i] < 0)
                {
                    rawFaces[i].Reverse();
                    result.Add(rawFaces[i]);
                }
            }
            return result;
        }


    }
}
