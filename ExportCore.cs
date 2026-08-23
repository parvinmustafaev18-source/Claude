using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    // ЯДРО ЭКСПОРТА В ЛИРА-САПР, отделённое от чертежа — как MeshCore для сетки.
    //
    // Раньше MESHEXPORTTXT делал всё одним методом на 796 строк: спрашивал параметры,
    // читал чертёж, собирал планарный граф, раздавал жёсткости, выдавливал стены и
    // писал файл. Проверить это можно было только руками в AutoCAD, поэтому экспорт —
    // единственная часть плагина, которую самотест не трогал, хотя именно он и есть
    // конечный результат работы.
    //
    // Здесь нет ни Editor, ни Transaction, ни Database: на входе — чистая геометрия,
    // на выходе — узлы, элементы и ГОТОВЫЙ ТЕКСТ задачи и легенды. Файлы пишет команда,
    // она же рисует круги проблем. Благодаря этому ExportSelfCheck может гонять экспорт
    // на выдуманных планах и проверять баланс площадей, висячие узлы и ссылки на
    // несуществующие жёсткости — без AutoCAD.
    internal class ExportInput
    {
        // Геометрия чертежа (мм).
        public List<Point2d> Contour = new List<Point2d>();
        // Отрезки планарного графа: контур + линии сетки + стены + кромки отверстий.
        public List<Point2d[]> Segments = new List<Point2d[]>();
        public List<List<Point2d>> HolePolys = new List<List<Point2d>>();
        public List<List<Point2d>> PylonRects = new List<List<Point2d>>();

        // Стены: исходные отрезки и их свойства (индексы совпадают).
        public List<Point2d[]> WallOrig = new List<Point2d[]>();
        public List<double> WallThickness = new List<double>();
        public List<bool> WallIsPylon = new List<bool>();
        public List<string> WallSizeKey = new List<string>();

        // Двери: отрезки поверх осей стен и их высоты (индексы совпадают).
        public List<Point2d[]> DoorSegs = new List<Point2d[]>();
        public List<double> DoorHeights = new List<double>();

        // Пилоны-стержни старых чертежей: центр сечения и его размеры.
        public List<Point2d> ColumnCenters = new List<Point2d>();
        public List<double[]> ColumnDims = new List<double[]>();

        // Параметры задачи.
        public double ThicknessMm = 300.0;
        public double ElasticModulus = 3.31e6;
        public double UnitWeight = 2.5;
        public double FloorHeight = 3000.0;
        public double WallStep = 300.0;
        public double PylonStiffFactor = 1.0;
        public string TaskName = "MESHPLUGIN_LIRA";

        // Сколько пилонов-стержней пришло без размеров в имени слоя (принято 400x400).
        // Считается при чтении чертежа, нужно только для сводки.
        public int ColumnsWithoutDims;
    }

    internal class ExportResult
    {
        public bool Ok;
        public string Error;
        public List<string> Log = new List<string>();

        // Готовые тексты: их остаётся только записать в файлы.
        public string TaskText = "";
        public string LegendText = "";

        // Схема задачи.
        public List<double[]> Nodes3 = new List<double[]>();   // узлы (мм)
        public List<int[]> Elements = new List<int[]>();       // {тип КЭ, № жёсткости, узлы...}
        public int SlabElemCount;                              // первые N элементов — пластины плиты

        // Числа для проверок и сводки.
        public double SlabArea, TargetArea, HolesArea;
        public int RectCount, QuadCount, TriCount;
        public int WallElemCount, BarCount, FanFaces, SpikeFans, MultiSpikeFaces;
        public int HoleElemsRemoved, PylonBodyElems, PylonBodyStiffId;
        public int DoorJambSplits, DoorPiers, DoorRowsSkipped, FailedFaces;
        public List<Point2d> LostFacePts = new List<Point2d>();
        public List<string> LostFaceCenters = new List<string>();
        public Dictionary<int, string> StiffTitles = new Dictionary<int, string>();
    }

    public partial class Commands
    {
        internal ExportResult BuildExportCore(ExportInput input)
        {
            var res = new ExportResult();

            var contourPts = input.Contour;
            // Список рвём: ядро режет отрезки и не должно портить вход вызывающего.
            var segments = new List<Point2d[]>(input.Segments);
            var holePolys = input.HolePolys;
            var pylonRects = input.PylonRects;
            var wallOrig = input.WallOrig;
            var wallOrigThickness = input.WallThickness;
            var wallOrigIsPylon = input.WallIsPylon;
            var wallOrigSizeKey = input.WallSizeKey;
            var doorOrig = input.DoorSegs;
            var doorHeights = input.DoorHeights;
            var columnCenters = input.ColumnCenters;
            var columnDims = input.ColumnDims;

            double thicknessMm = input.ThicknessMm;
            double thicknessM = thicknessMm / 1000.0;
            double elasticModulus = input.ElasticModulus;
            double unitWeight = input.UnitWeight;
            double floorHeight = input.FloorHeight;
            double wallStep = input.WallStep;
            double pylonStiffFactor = input.PylonStiffFactor;
            string taskName = input.TaskName;
            int columnsWithoutDims = input.ColumnsWithoutDims;

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

            segments = SplitSegmentsAtNodes(segments, 500.0, out _, out _);
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
                res.Log.Add($"\nКонцов стен внутри ячеек, врезанных в плиту веером треугольников: {spikeFans}\n");
            if (multiSpikeFaces > 0)
                res.Log.Add($"\nВНИМАНИЕ: ячеек с несколькими тупиковыми концами стен: {multiSpikeFaces} — связан только первый конец, проверьте сетку у этих стен\n");

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
                res.Error = "\nНе найдено ни одной замкнутой ячейки сетки — сначала постройте сетку (MESHQUADMESH).\n";
                return res;
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
                    res.Log.Add($"\nВНИМАНИЕ: высота дверного проёма {dh:0.#} >= высоты этажа {floorHeight:0.#} — стена под таким проёмом не будет выдавлена совсем (перемычки нет).\n");
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

            res.TaskText = sb.ToString();

            // Легенда: в текстовом формате ЛИРЫ у жёсткости нет ни имени, ни
            // комментария, поэтому расшифровка номеров пишется отдельным файлом
            // рядом с задачей — по нему видно, где стена, а где пилон.
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
            res.LegendText = lg.ToString();

            int rectCount = 0, quadCount = 0, triCount = 0;
            for (int i = 0; i < slabElemCount; i++)
                if (elements[i][0] == 41) rectCount++;
                else if (elements[i][0] == 44) quadCount++;
                else triCount++;

            res.Log.Add($"\nЭкспортировано: узлов {nodes3.Count}; плита: КЭ 41 {rectCount}, КЭ 44 {quadCount}, КЭ 42 {triCount} (вееров под пилонами: {fanFaces}); стены: КЭ 44 {wallElemCount} (толщин: {wallStiffIds.Count}); пилоны: стержней КЭ 10 {barCount} (сечений: {colStiffIds.Count})" +
                $"; осей пилонов (PILON) в чертеже: {wallOrigIsPylon.FindAll(p => p).Count}" +
                (holePolys.Count > 0 ? $"; отверстий: {holePolys.Count} (удалено элементов внутри: {holeElemsRemoved})" : "")
                + (pylonRects.Count > 0 ? $"; тела пилонов: контуров {pylonRects.Count}, элементов плиты в них {pylonBodyElems}" + (pylonBodyStiffId > 0 ? $" (жёсткость №{pylonBodyStiffId})" : "") : "") +
                (doorOrig.Count > 0 ? $"; дверных проёмов: {doorOrig.Count} (врезано узлов на косяках: {doorJambSplits}, кусков стены под дверью: {doorPiers}, пропущено рядов КЭ 44: {doorRowsSkipped})" : "") +
                (failedFaces > 0 ? $"; потеряно граней: {failedFaces}" : "") +
                (columnsWithoutDims > 0 ? $"; пилонов без размеров в имени слоя (принято 400x400): {columnsWithoutDims}" : "") + "\n");
            // Главная проверка результата: покрывают ли пластины плиту целиком.
            res.Log.AddRange(AreaBalanceLines(slabArea, targetArea, holesArea, slabElemCount));

            // Раскладка номеров — чтобы сверить с тем, что показала ЛИРА.
            res.Log.Add($"Жёсткости: 1 = плита H-{thicknessMm:0.#}\n");
            foreach (int sid in wallStiffOrdered)
                res.Log.Add($"  {sid} = {wallStiffTitle[sid]}\n");

            if (lostFaceCenters.Count > 0)
            {
                res.Log.Add($"\nВНИМАНИЕ: не удалось разбить ячеек: {lostFaceCenters.Count}, центры: {string.Join(", ", lostFaceCenters)} — в этих местах в ЛИРЕ будут дыры. Ячейки отмечены кругами в слое {ProblemLayerName}.\n");
            }
            res.Nodes3 = nodes3;
            res.Elements = elements;
            res.SlabElemCount = slabElemCount;
            res.SlabArea = slabArea;
            res.TargetArea = targetArea;
            res.HolesArea = holesArea;
            res.RectCount = rectCount;
            res.QuadCount = quadCount;
            res.TriCount = triCount;
            res.WallElemCount = wallElemCount;
            res.BarCount = barCount;
            res.FanFaces = fanFaces;
            res.SpikeFans = spikeFans;
            res.MultiSpikeFaces = multiSpikeFaces;
            res.HoleElemsRemoved = holeElemsRemoved;
            res.PylonBodyElems = pylonBodyElems;
            res.PylonBodyStiffId = pylonBodyStiffId;
            res.DoorJambSplits = doorJambSplits;
            res.DoorPiers = doorPiers;
            res.DoorRowsSkipped = doorRowsSkipped;
            res.FailedFaces = failedFaces;
            res.LostFacePts = lostFacePts;
            res.LostFaceCenters = lostFaceCenters;
            res.StiffTitles = wallStiffTitle;
            res.Ok = true;
            return res;
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
