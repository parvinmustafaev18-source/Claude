using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    // РАСЧЁТНОЕ ЯДРО ПОСТРОЕНИЯ СЕТКИ, отделённое от чертежа.
    //
    // Раньше MESHQUADMESH делал всё сразу: спрашивал пользователя, читал чертёж,
    // считал сетку и рисовал её — одним методом на 600+ строк. Прогнать расчёт
    // иначе как руками в AutoCAD было нельзя, поэтому каждая ошибка находилась
    // на живом объекте и стоила круга «закрыть AutoCAD → собрать → открыть».
    //
    // Здесь расчёт живёт отдельно: на входе — только геометрия (MeshInput), на
    // выходе — только геометрия и числа (MeshResult). Ни Editor, ни Transaction,
    // ни Database ядро не видит, поэтому его можно вызвать сколько угодно раз
    // подряд на выдуманных планах — на этом стоит самотест (MESHSELFTEST).
    // Чтение чертежа, снап объектов к сетке и отрисовка остались в команде.

    // Всё, что построение берёт из чертежа. Списки пустые, а не null: ядро их
    // перебирает без проверок, как и раньше перебирало результаты чтения слоёв.
    internal class MeshInput
    {
        public List<Point2d> Contour = new List<Point2d>();          // контур плиты (против часовой)
        public double CellSize = 300.0;                              // сторона элемента, мм
        public List<Point2d[]> WallSegments = new List<Point2d[]>(); // слой WALLS
        public List<Point2d> DoorEnds = new List<Point2d>();         // косяки проёмов — узлы сетки
        public List<List<Point2d>> ColumnPolys = new List<List<Point2d>>();  // COLUMNS: пустота
        public List<List<Point2d>> HolePolys = new List<List<Point2d>>();    // MESH_HOLES: пустота
        public List<List<Point2d>> PylonRects = new List<List<Point2d>>();   // MESH_PYLONS: мелкая сетка
        public List<Point2d[]> PylonCrosses = new List<Point2d[]>();         // поперечные оси пилонов

        // Мягкие цели выравнивания линий сетки: косяки дверей и оси пилонов-пластин.
        public List<double> JambXs = new List<double>();
        public List<double> JambYs = new List<double>();
        public List<double> AxisXs = new List<double>();
        public List<double> AxisYs = new List<double>();

        // Двери подтягиваются к УЖЕ ПОСТРОЕННОЙ сетке, а это правка чертежа —
        // ядру она недоступна. Поэтому хост (команда) получает координаты линий
        // сетки, двигает двери сам и возвращает поперечные разрезы через косяки;
        // в общий журнал он пишет через переданный список. null — дверей нет.
        public Func<List<double>, List<double>, List<string>, List<Point2d[]>> SnapDoors;
    }

    // Результат расчёта: геометрия для отрисовки, сообщения и числа постусловий.
    // Числа продублированы полями (а не только текстом в Log) ровно затем, чтобы
    // самотест мог вынести вердикт, не разбирая строки.
    internal class MeshResult
    {
        public bool Ok;                                                  // false — нарушено жёсткое правило
        public List<Point2d> ErrorPts = new List<Point2d>();             // места нарушения — круги ПРОБЛЕМА
        public List<string> Log = new List<string>();                    // то, что раньше шло в ed.WriteMessage
        public List<Point2d[]> Segments = new List<Point2d[]>();         // итоговые линии сетки
        public List<Point2d> ProblemPts = new List<Point2d>();           // проблемные места сетки

        public int FailedPolygons;        // не удалось триангулировать
        public int UnlinkedPylonNodes;    // узлы контура пилона вне сетки
        public int CrossingsLeft;         // пересечения линий без узла
        public int PoorElements;          // элементы с качеством ниже MinQualityAlpha
        public double WorstAlpha = 1.0;

        // Постусловия из RunMeshSelfCheck.
        public int OutsideContour, InsideVoid, ShortEdges, DuplicateEdges, DegenerateEdges;
    }

    public partial class Commands
    {
        // Построение сетки по готовой геометрии. Порядок этапов и все допуски —
        // те же, что были внутри MESHQUADMESH; перенос сделан без изменения логики.
        internal MeshResult BuildMeshCore(MeshInput input)
        {
            var res = new MeshResult();

            var contourPts = input.Contour;
            double cellSize = input.CellSize;
            var wallSegments = input.WallSegments;
            var doorEnds = input.DoorEnds;
            var columnPolys = input.ColumnPolys;
            var holePolys = input.HolePolys;
            var pylonRects = input.PylonRects;
            var pylonCrosses = input.PylonCrosses;

            var bb = PolygonBBox(contourPts);
            double minX = bb[0], minY = bb[1], maxX = bb[2], maxY = bb[3];

            // ЖЁСТКОЕ ПРАВИЛО: стены, пилоны и отверстия целиком лежат в пределах
            // плиты (касание границы допустимо). Нарушение — расчёт не начинается.
            var outsideWalls = new List<string>();
            var outsideWallPts = new List<Point2d>();
            foreach (var w in wallSegments)
            {
                if (IsSegmentInsideContour(w[0], w[1], contourPts)) continue;
                Point2d wm = new Point2d((w[0].X + w[1].X) / 2.0, (w[0].Y + w[1].Y) / 2.0);
                outsideWalls.Add($"({wm.X:0}, {wm.Y:0})");
                outsideWallPts.Add(wm);
            }
            if (outsideWalls.Count > 0)
            {
                res.Log.Add($"\nОшибка: сегменты стен выходят за контур фундаментной плиты ({outsideWalls.Count} шт.), середины: {string.Join(", ", outsideWalls)}. Стена обязана целиком лежать в пределах плиты. Команда остановлена, чертёж не изменён. Проблемные места отмечены кругами в слое {ProblemLayerName}.\n");
                res.ErrorPts = outsideWallPts;
                return res;
            }

            // Жёсткий запрет: контур пилона ни при каких условиях не может выходить
            // за пределы фундаментной плиты (касание границы допустимо). Нарушение —
            // остановка команды без изменений в чертеже.
            var outsideColumns = new List<string>();
            var outsideColumnPts = new List<Point2d>();
            foreach (var col in columnPolys)
            {
                if (IsPolygonInsideContour(col, contourPts)) continue;
                var cc = ComputeColumnCenters(new List<List<Point2d>> { col })[0];
                outsideColumns.Add($"({cc.X:0}, {cc.Y:0})");
                outsideColumnPts.Add(cc);
            }
            if (outsideColumns.Count > 0)
            {
                res.Log.Add($"\nОшибка: пилоны выходят за контур фундаментной плиты ({outsideColumns.Count} шт.), центры: {string.Join(", ", outsideColumns)}. Пилон обязан целиком лежать в пределах плиты. Команда остановлена, чертёж не изменён. Проблемные места отмечены кругами в слое {ProblemLayerName}.\n");
                res.ErrorPts = outsideColumnPts;
                return res;
            }

            // Жёсткий запрет (как для стен и пилонов): контур отверстия не может
            // выходить за пределы фундаментной плиты (касание границы допустимо).
            var outsideHoles = new List<string>();
            var outsideHolePts = new List<Point2d>();
            foreach (var h in holePolys)
            {
                if (IsPolygonInsideContour(h, contourPts)) continue;
                var hc = PolygonCentroid(h);
                outsideHoles.Add($"({hc.X:0}, {hc.Y:0})");
                outsideHolePts.Add(hc);
            }
            if (outsideHoles.Count > 0)
            {
                res.Log.Add($"\nОшибка: контуры отверстий выходят за контур фундаментной плиты ({outsideHoles.Count} шт.), центры: {string.Join(", ", outsideHoles)}. Отверстие обязано целиком лежать в пределах плиты. Команда остановлена, чертёж не изменён. Проблемные места отмечены кругами в слое {ProblemLayerName}.\n");
                res.ErrorPts = outsideHolePts;
                return res;
            }

            // Тот же жёсткий запрет, что для стен, пилонов и отверстий.
            var outsideRects = new List<string>();
            var outsideRectPts = new List<Point2d>();
            foreach (var r in pylonRects)
            {
                if (IsPolygonInsideContour(r, contourPts)) continue;
                var rc = PolygonCentroid(r);
                outsideRects.Add($"({rc.X:0}, {rc.Y:0})");
                outsideRectPts.Add(rc);
            }
            if (outsideRects.Count > 0)
            {
                res.Log.Add($"\nОшибка: контуры пилонов выходят за контур фундаментной плиты ({outsideRects.Count} шт.), центры: {string.Join(", ", outsideRects)}. Пилон обязан целиком лежать в пределах плиты. Команда остановлена, чертёж не изменён. Проблемные места отмечены кругами в слое {ProblemLayerName}.\n");
                res.ErrorPts = outsideRectPts;
                return res;
            }

            var cutSegments = new List<Point2d[]>(wallSegments);
            foreach (var col in columnPolys)
            {
                int cn = col.Count;
                for (int i = 0; i < cn; i++)
                    cutSegments.Add(new Point2d[] { col[i], col[(i + 1) % cn] });
            }
            // Стороны отверстий врезаются в сетку как грани пилонов: узлы садятся
            // на кромку, ячейки режутся по ней, внутренняя часть выбрасывается.
            foreach (var h in holePolys)
            {
                int hn = h.Count;
                for (int i = 0; i < hn; i++)
                    cutSegments.Add(new Point2d[] { h[i], h[(i + 1) % hn] });
            }

            // Принудительный поперечный узел в центре пилона: для каждой оси-стены
            // PILON строим перпендикуляр через её середину. Он идёт ТОЛЬКО в список
            // разреза ячеек (splitConstraints) — режет ячейки и даёт узел в центре
            // поперёк оси, но НЕ попадает в cutSegments, чтобы ResolveOverlappingSegments
            // не удалил это поперечное ребро как совпавшее со «стеной».
            // У пилона с отпечатком крест не нужен: узел в центре даёт сама мелкая
            // сетка (её линии проходят и по оси, и поперёк неё), а лишний разрез
            // режет ячейку по бесконечной прямой и плодит косые рёбра вокруг пилона.
            var splitConstraints = new List<Point2d[]>(cutSegments);
            int crossesKept = 0, crossesDropped = 0;
            foreach (var c in pylonCrosses)
            {
                Point2d cmid = new Point2d((c[0].X + c[1].X) / 2.0, (c[0].Y + c[1].Y) / 2.0);
                if (PointInOrOnAnyPolygon(cmid, pylonRects)) { crossesDropped++; continue; }
                splitConstraints.Add(c);
                crossesKept++;
            }
            if (crossesKept > 0)
                res.Log.Add($"\nПоперечных осей пилонов врезано через центр: {crossesKept}\n");
            if (crossesDropped > 0)
                res.Log.Add($"\nПоперечных осей не потребовалось (узел в центре даёт отпечаток): {crossesDropped}\n");

            // Стороны отпечатка НЕ идут ни в cutSegments, ни в splitConstraints.
            // В cutSegments они были бы «стеной», и ResolveOverlappingSegments снял
            // бы сам отпечаток. В splitConstraints — резали бы ячейку по БЕСКОНЕЧНОЙ
            // прямой (SplitPolygonByWalls режет полуплоскостями), то есть далеко за
            // пределами пилона: отсюда и брались длинные косые рёбра вокруг него.
            // Ячейку, задетую отпечатком, режет прямоугольная разность ниже.
            // Здесь стороны нужны только как запрет на слияние треугольников через
            // грань пилона.
            var pylonEdges = new List<Point2d[]>();
            foreach (var r in pylonRects)
            {
                int rn = r.Count;
                for (int i = 0; i < rn; i++)
                    pylonEdges.Add(new Point2d[] { r[i], r[(i + 1) % rn] });
            }
            var mergeBlockers = new List<Point2d[]>(splitConstraints);
            mergeBlockers.AddRange(pylonEdges);

            // Косяки дверных проёмов: первый проход собирает только их координаты —
            // они идут «мягкими» целями в BuildGridCoords. Сами поперечные
            // ограничения строятся ниже, ПОСЛЕ снапа дверей к готовой сетке, иначе
            // разрезы остались бы на старых местах.
            var jambXs = input.JambXs;
            var jambYs = input.JambYs;

            var quadCells = new List<Point2d[]>();
            var boundaryCells = new List<Point2d[]>();
            var wallCells = new List<Point2d[]>();

            // Линии сетки допускается смещать к граням пилонов для чистоты разбиения
            // (увеличение ячейки ≤30% шага, но не более 100 мм).
            var colXs = new List<double>();
            var colYs = new List<double>();
            foreach (var col in columnPolys)
            {
                foreach (var p in col)
                {
                    colXs.Add(p.X);
                    colYs.Add(p.Y);
                }
            }

            // Грани отпечатка — мягкие цели: если линия сетки рядом, она садится
            // ровно на грань пилона, и полосы-огрызки между гранью и линией не
            // остаётся. Жёсткой целью не делаем — вставлять ради каждого пилона
            // линию через весь план накладно (на плане их бывает под сотню).
            foreach (var r in pylonRects)
            {
                foreach (var p in r)
                {
                    colXs.Add(p.X);
                    colYs.Add(p.Y);
                }
            }

            // Кромки отверстий — «жёсткие» цели: на каждой обязана лежать линия сетки.
            // Если ближайшая линия рядом (в пределах допуска) — двигаем её на кромку,
            // иначе ВСТАВЛЯЕМ новую линию. Тогда прямоугольный проём ложится на целые
            // ячейки, неполных ячеек по периметру нет, узлы зашивать не нужно —
            // круги ПРОБЛЕМА у кромок исчезают по построению.
            var holeXs = new List<double>();
            var holeYs = new List<double>();
            foreach (var h in holePolys)
            {
                foreach (var p in h)
                {
                    holeXs.Add(p.X);
                    holeYs.Add(p.Y);
                }
            }

            // Косяки — «мягкие» цели наравне с гранями пилонов: линия сетки, если она
            // рядом, садится точно на косяк, и поперечный разрез не оставляет узкой
            // полосы. Жёсткой целью косяк не делаем — вставлять ради двери линию
            // через весь план накладно, локального разреза ячеек достаточно.
            colXs.AddRange(jambXs);
            colYs.AddRange(jambYs);

            // Оси пилонов-пластин: концы и центр (там узел от поперечного разреза
            // креста). Без этого линия сетки проходит в 20–80 мм от них и режет
            // пластину на КЭ в единицы миллиметров.
            colXs.AddRange(input.AxisXs);
            colYs.AddRange(input.AxisYs);

            var xs = BuildGridCoords(minX, maxX, cellSize, colXs, holeXs, out int shiftedX, out int insertedX, out int rejectedX);
            var ys = BuildGridCoords(minY, maxY, cellSize, colYs, holeYs, out int shiftedY, out int insertedY, out int rejectedY);
            if (shiftedX + shiftedY > 0)
                res.Log.Add($"\nЛиний сетки смещено к граням пилонов/кромкам отверстий/косякам: {shiftedX + shiftedY}\n");
            if (insertedX + insertedY > 0)
                res.Log.Add($"\nЛиний сетки добавлено по кромкам отверстий: {insertedX + insertedY}\n");
            if (rejectedX + rejectedY > 0)
                res.Log.Add($"\nЦелей выравнивания пропущено (линия занята другой целью или сдвиг оставил бы полосу уже {MeshTol.MinGridGap(cellSize):0} мм): {rejectedX + rejectedY}\n");

            // Сетка построена — теперь хост подтягивает к ней двери (снап меняет
            // чертёж, поэтому делает это команда) и возвращает поперечные разрезы
            // через косяки уже по новым местам. В самотесте дверей нет — вызова тоже.
            var doorJambs = input.SnapDoors != null
                ? input.SnapDoors(xs, ys, res.Log)
                : new List<Point2d[]>();
            splitConstraints.AddRange(doorJambs);
            if (doorJambs.Count > 0)
                res.Log.Add($"\nКосяков дверных проёмов врезано в сетку: {doorJambs.Count}\n");

            for (int xi = 0; xi + 1 < xs.Count; xi++)
            {
                for (int yi = 0; yi + 1 < ys.Count; yi++)
                {
                    Point2d[] cell = new Point2d[]
                    {
                        new Point2d(xs[xi], ys[yi]),
                        new Point2d(xs[xi + 1], ys[yi]),
                        new Point2d(xs[xi + 1], ys[yi + 1]),
                        new Point2d(xs[xi], ys[yi + 1])
                    };

                    if (CellInsideAnyColumn(cell, columnPolys))
                    {
                        continue; // внутри пилона-стержня (COLUMNS) сетки плиты нет
                    }

                    if (pylonRects.Count > 0 && !CellInsideAnyRect(cell, pylonRects)
                        && CellOverlapsAnyRect(cell, pylonRects))
                    {
                        // Ячейка задета отпечатком с краю. Режем её ПРЯМОУГОЛЬНОЙ
                        // РАЗНОСТЬЮ, а не полуплоскостями по граням: полуплоскость
                        // продолжает грань пилона через всю ячейку и дальше, из-за
                        // чего вокруг пилона появлялись длинные косые рёбра и вееры
                        // треугольников. Разность даёт до четырёх прямоугольников,
                        // каждый из которых идёт по обычному пути классификации.
                        foreach (var sub in SubtractRects(cell, pylonRects))
                        {
                            if (CellTouchesWalls(sub, splitConstraints)) wallCells.Add(sub);
                            else if (IsCellFullyInside(sub, contourPts)) quadCells.Add(sub);
                            else boundaryCells.Add(sub);
                        }
                        continue;
                    }

                    if (CellInsideAnyRect(cell, pylonRects))
                    {
                        // Ячейка целиком накрыта отпечатком пилона — её место
                        // займут мелкие ячейки, построенные ниже по граням пилона.
                        // Проверка по габаритам, а не по IsPointInPolygon: после
                        // снапа грань отпечатка обычно совпадает с линией сетки, и
                        // углы ячейки лежат НА границе прямоугольника, где проверка
                        // «строго внутри» даёт false, а совпадающие стороны не дают
                        // и пересечения — ячейка проскочила бы обе проверки и легла
                        // поверх мелкой сетки вторым слоем.
                        continue;
                    }

                    if (CellCenterInsideAnyColumn(cell, holePolys))
                    {
                        // Внутри проёма сетки нет вообще. Проверяем по ЦЕНТРУ ячейки,
                        // а НЕ по всем углам: у ячейки, чья внешняя грань лежит ровно
                        // на кромке проёма, 2 угла на границе (IsPointInPolygon → false),
                        // поэтому CellInsideAnyColumn её не выбрасывал — она резалась
                        // позже и оставляла осиротевший узел с кругом ПРОБЛЕМА на шаг
                        // внутрь кромки. Центр такой ячейки строго внутри → выброс.
                        continue;
                    }

                    if (CellTouchesWalls(cell, splitConstraints))
                    {
                        wallCells.Add(cell);
                    }
                    else if (IsCellFullyInside(cell, contourPts))
                    {
                        quadCells.Add(cell);
                    }
                    else
                    {
                        boundaryCells.Add(cell);
                    }
                }
            }

            // Мелкая сетка внутри отпечатка пилона. Строится своими координатами,
            // а не общей сеткой плиты: шаг там ~100 мм, и линии обязаны пройти по
            // граням и по оси пилона. Ячейки плиты, попавшие внутрь отпечатка,
            // выброшены выше, а куски у грани отбрасываются по центроиду ниже, —
            // поэтому наложения двух сеток нет.
            int pylonInnerCells = 0;
            double thinnestPylonSide = double.MaxValue;
            foreach (var r in pylonRects)
            {
                double rx0 = r[0].X, ry0 = r[0].Y, rx1 = r[2].X, ry1 = r[2].Y;
                var fx = BuildPylonInnerCoords(rx0, rx1);
                var fy = BuildPylonInnerCoords(ry0, ry1);

                for (int i = 0; i + 1 < fx.Count; i++)
                {
                    for (int j = 0; j + 1 < fy.Count; j++)
                    {
                        quadCells.Add(new Point2d[]
                        {
                            new Point2d(fx[i], fy[j]),
                            new Point2d(fx[i + 1], fy[j]),
                            new Point2d(fx[i + 1], fy[j + 1]),
                            new Point2d(fx[i], fy[j + 1])
                        });
                        pylonInnerCells++;

                        double side = Math.Min(fx[i + 1] - fx[i], fy[j + 1] - fy[j]);
                        if (side < thinnestPylonSide) thinnestPylonSide = side;
                    }
                }
            }
            if (pylonInnerCells > 0)
            {
                res.Log.Add($"\nОтпечаток пилонов: контуров {pylonRects.Count}, мелких элементов внутри: {pylonInnerCells} (шаг ~{MeshTol.PylonInnerCell:0} мм)\n");
                if (thinnestPylonSide < MinElementSize)
                    res.Log.Add($"ВНИМАНИЕ: самый узкий элемент внутри пилона {thinnestPylonSide:0} мм — меньше минимального размера КЭ ({MinElementSize:0} мм). Так выходит у пилонов тоньше {2 * MeshTol.PylonInnerCell:0} мм: половина толщины и есть ширина элемента.\n");
            }

            res.Log.Add($"\nПостроено квадратных элементов: {quadCells.Count}, ячеек у стен: {wallCells.Count}\n");

            var allSegments = new List<Point2d[]>();

            foreach (var cell in quadCells)
            {
                AddQuadSegments(allSegments, cell);
            }

            // Кайма вдоль границы: каждую неполную ячейку сетки обрезаем по контуру
            // напрямую (Sutherland-Hodgman), без триангуляции всей полосы.
            var triVerts = new List<Point2d[]>();
            var directQuads = new List<Point2d[]>();
            int failedPolygons = 0;
            // Центры полигонов, которые не удалось триангулировать, — для кругов
            // в слое проблем (сетка в этих местах не построится).
            var failedPolygonPts = new List<Point2d>();

            foreach (var cell in boundaryCells)
            {
                var clipped = ClipPolygonToConvexCell(contourPts, cell);
                clipped = CleanupPolygon(clipped);

                if (clipped.Count < 3) continue;
                if (Math.Abs(PolygonArea(clipped)) < MeshTol.MinArea) continue;

                if (clipped.Count == 4 && IsConvexQuad(clipped.ToArray()))
                {
                    directQuads.Add(clipped.ToArray());
                }
                else
                {
                    int fBefore = failedPolygons;
                    foreach (var tri in TriangulateSimplePolygon(clipped, ref failedPolygons))
                    {
                        if (Math.Abs(PolygonArea(new List<Point2d>(tri))) < MeshTol.MinArea) continue;
                        triVerts.Add(tri);
                    }
                    if (failedPolygons > fBefore) failedPolygonPts.Add(PolygonCentroid(clipped));
                }
            }

            // Ячейки, задетые стеной: ячейка разрезается по линии стены на части
            // (Sutherland-Hodgman по обеим полуплоскостям) — стена становится рёбрами
            // сетки с общими узлами, элементы у стены уменьшаются, но не более чем вдвое.
            foreach (var cell in wallCells)
            {
                var clipped = ClipPolygonToConvexCell(contourPts, cell);
                clipped = CleanupPolygon(clipped);

                if (clipped.Count < 3) continue;
                if (Math.Abs(PolygonArea(clipped)) < MeshTol.MinArea) continue;

                foreach (var piece in SplitPolygonByWalls(clipped, splitConstraints))
                {
                    if (piece.Count < 3) continue;
                    if (Math.Abs(PolygonArea(piece)) < MeshTol.MinArea) continue;
                    if (PieceInsideAnyColumn(piece, columnPolys)) continue;
                    if (PieceInsideAnyColumn(piece, holePolys)) continue;
                    // Кусок ячейки, отрезанный гранью отпечатка внутрь пилона:
                    // там уже лежит своя мелкая сетка.
                    if (PieceInsideAnyColumn(piece, pylonRects)) continue;

                    if (piece.Count == 4 && IsConvexQuad(piece.ToArray()))
                    {
                        directQuads.Add(piece.ToArray());
                    }
                    else
                    {
                        int fBefore = failedPolygons;
                        foreach (var tri in TriangulateSimplePolygon(piece, ref failedPolygons))
                        {
                            if (Math.Abs(PolygonArea(new List<Point2d>(tri))) < MeshTol.MinArea) continue;
                            triVerts.Add(tri);
                        }
                        if (failedPolygons > fBefore) failedPolygonPts.Add(PolygonCentroid(piece));
                    }
                }
            }

            foreach (var quad in directQuads)
            {
                AddQuadSegments(allSegments, quad);
            }

            res.Log.Add($"\nТреугольников по краю (до объединения): {triVerts.Count}\n");

            // Справочник "сторона -> какие треугольники её используют".
            // Узлы треугольников проходят через общий NodeIndex: сторона двух
            // соседних треугольников опознаётся как общая по допуску слияния,
            // а не по совпадению округлённых координат.
            var triNodes = new NodeIndex();
            var edgeMap = new Dictionary<long, List<int>>();

            for (int i = 0; i < triVerts.Count; i++)
            {
                var t = triVerts[i];
                long[] keys = new long[]
                {
                    EdgePairKey(triNodes.GetNode(t[0]), triNodes.GetNode(t[1])),
                    EdgePairKey(triNodes.GetNode(t[1]), triNodes.GetNode(t[2])),
                    EdgePairKey(triNodes.GetNode(t[2]), triNodes.GetNode(t[0]))
                };

                foreach (var k in keys)
                {
                    if (!edgeMap.ContainsKey(k))
                        edgeMap[k] = new List<int>();
                    edgeMap[k].Add(i);
                }
            }

            // Жадное объединение пар треугольников в четырёхугольники
            bool[] used = new bool[triVerts.Count];
            var mergedQuads = new List<Point2d[]>();

            for (int i = 0; i < triVerts.Count; i++)
            {
                if (used[i]) continue;

                var t = triVerts[i];
                long[] keys = new long[]
                {
                    EdgePairKey(triNodes.GetNode(t[0]), triNodes.GetNode(t[1])),
                    EdgePairKey(triNodes.GetNode(t[1]), triNodes.GetNode(t[2])),
                    EdgePairKey(triNodes.GetNode(t[2]), triNodes.GetNode(t[0]))
                };
                Point2d[] edgeStart = new Point2d[] { t[0], t[1], t[2] };
                Point2d[] edgeEnd = new Point2d[] { t[1], t[2], t[0] };
                Point2d[] opposite = new Point2d[] { t[2], t[0], t[1] };

                bool merged = false;

                for (int side = 0; side < 3 && !merged; side++)
                {
                    // Правило ЛИРЫ: не объединять, если через общую сторону проходят
                    // другие элементы — ребро должно принадлежать ровно двум
                    // треугольникам и не лежать на стене/грани пилона.
                    if (edgeMap[keys[side]].Count != 2) continue;

                    Point2d edgeMid = new Point2d(
                        (edgeStart[side].X + edgeEnd[side].X) / 2.0,
                        (edgeStart[side].Y + edgeEnd[side].Y) / 2.0);
                    bool onWall = false;
                    foreach (var w in mergeBlockers)
                        if (IsPointOnSegment(edgeMid, w[0], w[1], MeshTol.OnSegment)) { onWall = true; break; }
                    if (onWall) continue;

                    foreach (int j in edgeMap[keys[side]])
                    {
                        if (j == i || used[j]) continue;

                        Point2d d = FindOppositeVertex(triVerts[j], edgeStart[side], edgeEnd[side]);
                        Point2d a = edgeStart[side];
                        Point2d b = edgeEnd[side];
                        Point2d c = opposite[side];

                        Point2d[] quad = new Point2d[] { a, c, b, d };

                        // Сливать только если четырёхугольник не вырожден по углам:
                        // выпуклый, но игольчатый квад хуже двух нормальных треугольников.
                        if (IsConvexQuad(quad) && QuadShapeOk(quad))
                        {
                            mergedQuads.Add(quad);
                            used[i] = true;
                            used[j] = true;
                            merged = true;
                            break;
                        }
                    }
                }
            }

            int leftoverCount = 0;

            foreach (var quad in mergedQuads)
            {
                AddQuadSegments(allSegments, quad);
            }

            for (int i = 0; i < triVerts.Count; i++)
            {
                if (used[i]) continue;
                leftoverCount++;

                AddTriSegments(allSegments, triVerts[i]);
            }

            res.Log.Add($"\nПрямых четырёхугольников по краю: {directQuads.Count}, получено четырёхугольников из объединения: {mergedQuads.Count}, осталось одиночных треугольников: {leftoverCount}\n");

            // Контроль качества: дальше элементы превращаются в отрезки и их форма
            // теряется, поэтому вырожденные элементы пересчитываются здесь.
            res.FailedPolygons = failedPolygons;
            if (failedPolygons > 0)
                res.Log.Add($"\nВНИМАНИЕ: не удалось триангулировать полигонов: {failedPolygons} — возможны дыры в сетке по краю или у стен.\n");

            int poorTris = 0, poorQuads = 0;
            double worstAlpha = 1.0;
            for (int i = 0; i < triVerts.Count; i++)
            {
                if (used[i]) continue;
                double alpha = TriangleAlpha(triVerts[i][0], triVerts[i][1], triVerts[i][2]);
                if (alpha < worstAlpha) worstAlpha = alpha;
                if (alpha < MinQualityAlpha) poorTris++;
            }
            foreach (var q in directQuads)
            {
                double alpha = QuadAlpha(q);
                if (alpha < worstAlpha) worstAlpha = alpha;
                if (alpha < MinQualityAlpha) poorQuads++;
            }
            res.PoorElements = poorTris + poorQuads;
            res.WorstAlpha = worstAlpha;
            if (poorTris + poorQuads > 0)
                res.Log.Add($"\nВНИМАНИЕ: элементов с качеством α<{MinQualityAlpha:0.0#} (по методике ЛИРА-САПР): треугольников: {poorTris}, четырёхугольников: {poorQuads}, худший α={worstAlpha:0.00}\n");

            var uniqueSegments = DeduplicateSegments(allSegments);
            var innerSegments = RemoveSegmentsOnContour(uniqueSegments, contourPts, out int removedOnContour);
            innerSegments = ResolveOverlappingSegments(innerSegments, cutSegments, out int removedOnWalls, out int mergedOverlaps);

            // Пустоты для функций зашивания сетки: пилоны И отверстия. Внутрь и той,
            // и другой сетка не заходит, а узлы на их кромке — граничные (фиксированные,
            // не «открытые»). Функции WeldShortNodes/CloseOpenNodes/SmoothMesh
            // используют этот список только для обработки границы пустоты, поэтому
            // отверстия обрабатываются наравне с пилонами без правок внутри них.
            // Пилон-специфичный EnsureColumnCornerLinks по-прежнему получает columnPolys.
            var voidPolys = new List<List<Point2d>>(columnPolys);
            voidPolys.AddRange(holePolys);

            // Рёбра короче MinElementSize (100 мм) недопустимы: подвижные узлы сетки
            // смещаются к неподвижной геометрии или сливаются друг с другом.
            innerSegments = WeldShortNodes(innerSegments, wallSegments, voidPolys, contourPts, pylonRects, out int weldedEdges);
            if (weldedEdges > 0)
            {
                innerSegments = DeduplicateSegments(innerSegments);
                innerSegments = RemoveSegmentsOnContour(innerSegments, contourPts, out _);
                innerSegments = ResolveOverlappingSegments(innerSegments, cutSegments, out _, out _);
            }

            // Каждый угол пилона обязан быть связан с сеткой минимум в двух направлениях.
            innerSegments = EnsureColumnCornerLinks(innerSegments, columnPolys, cellSize, out int cornerLinks);

            // Линия не может обрываться посреди другого элемента: узел, лежащий
            // внутри чужого отрезка, делит его на два (общий узел для обоих).
            int splitDupsTotal = 0;
            innerSegments = SplitSegmentsAtNodes(innerSegments, cellSize, out int splitEdges, out int splitDupsA);
            splitDupsTotal += splitDupsA;

            // Открытые узлы недопустимы: точка, упершаяся в линию, замыкается
            // наклонной в соседний узел (угол по возможности близок к 30/45°).
            var unclosedNodes = new List<Point2d>();
            innerSegments = CloseOpenNodes(innerSegments, cutSegments, contourPts, voidPolys, cellSize, out int closedNodes, unclosedNodes);
            if (closedNodes > 0)
            {
                innerSegments = SplitSegmentsAtNodes(innerSegments, cellSize, out _, out int splitDupsB);
                splitDupsTotal += splitDupsB;
            }

            // Финальный шаг: сглаживание подвижных узлов для повышения качества α.
            innerSegments = SmoothMesh(innerSegments, cutSegments, contourPts, voidPolys, pylonRects, xs, ys, out int smoothedNodes);
            if (smoothedNodes > 0)
                res.Log.Add($"\nСглажено узлов (Лаплас): {smoothedNodes}\n");

            // Жёсткое правило: перед отрисовкой ничто не выходит за контур плиты.
            // Отрезок, пересекающий контур, не удаляется целиком (после удаления
            // сетка не дотягивалась до границы), а подрезается: наружная часть
            // отбрасывается, конец внутренней части ложится точно на контур.
            innerSegments = ClipSegmentsToContour(innerSegments, contourPts, out int clippedToContour, out int removedOutside);
            if (clippedToContour > 0)
                res.Log.Add($"\nПодрезано отрезков по контуру плиты: {clippedToContour}\n");
            if (removedOutside > 0)
                res.Log.Add($"\nВНИМАНИЕ: удалено отрезков целиком вне контура плиты: {removedOutside}\n");

            // Жёсткое правило: внутренность пилона всегда пуста (только точка в
            // центре). Любой отрезок, залезший внутрь контура пилона, подрезается
            // по его сторонам; части строго внутри отбрасываются.
            innerSegments = ClipSegmentsOutsideColumns(innerSegments, columnPolys, out int clippedAtColumns, out int removedInColumns);
            if (clippedAtColumns + removedInColumns > 0)
                res.Log.Add($"\nОчистка внутренностей пилонов: подрезано отрезков: {clippedAtColumns}, удалено целиком внутри: {removedInColumns}\n");

            // То же правило для отверстий: внутри проёма ничего не остаётся.
            if (holePolys.Count > 0)
            {
                innerSegments = ClipSegmentsOutsideColumns(innerSegments, holePolys, out int clippedAtHoles, out int removedInHoles);
                if (clippedAtHoles + removedInHoles > 0)
                    res.Log.Add($"\nОчистка отверстий: подрезано отрезков: {clippedAtHoles}, удалено целиком внутри: {removedInHoles}\n");
            }

            // Узлы на косяках дверных проёмов: рёбра стены (и любые рёбра, проходящие
            // через конец дверного отрезка) режем ровно в этих точках, чтобы кусок
            // стены точно совпал с проёмом при экспорте.
            if (doorEnds.Count > 0)
            {
                innerSegments = SplitSegmentsAtPoints(innerSegments, doorEnds, MeshTol.DoorOnAxis, out int doorSplits);
                if (doorSplits > 0)
                    res.Log.Add($"\nУзлов сетки врезано на косяках дверных проёмов: {doorSplits}\n");
            }

            // ЖЁСТКОЕ ПРАВИЛО: все узлы контура пилона входят в сетку плиты. Сначала
            // врезка (ребро, прошедшее через узел насквозь, режется в нём), затем
            // проверка постусловия — то, что осталось непривязанным, идёт в круги
            // ПРОБЛЕМА, а не замалчивается.
            var pylonNodes = CollectPylonOutlineNodes(pylonRects);
            var unlinkedPylonNodes = new List<Point2d>();
            if (pylonNodes.Count > 0)
            {
                innerSegments = SplitSegmentsAtPylonNodes(innerSegments, pylonNodes, cellSize, out int pylonNodeSplits);
                unlinkedPylonNodes = FindUnlinkedPylonNodes(innerSegments, pylonNodes, cellSize);
                res.UnlinkedPylonNodes = unlinkedPylonNodes.Count;

                res.Log.Add($"\nУзлы контуров пилонов: всего {pylonNodes.Count}, врезано в проходящие рёбра: {pylonNodeSplits}, не вошло в сетку: {unlinkedPylonNodes.Count}\n");
                if (unlinkedPylonNodes.Count > 0)
                    res.Log.Add($"ВНИМАНИЕ: часть узлов контура пилонов не связана с сеткой плиты — отмечены кругами в слое {ProblemLayerName}. Обычная причина: грань пилона прошла в паре миллиметров от линии сетки и полосу схлопнула сварка коротких рёбер.\n");
            }

            // ЖЁСТКОЕ ПРАВИЛО: линии сетки не пересекаются без узла. Пересечение
            // без общего узла в ЛИРЕ не связывает элементы (планарный граф строится
            // по узлам), а в чертеже выглядит как крест посреди элемента. Источники
            // такие пересечения имеют разные (замыкания открытых узлов, наклонные
            // после подрезки), поэтому правило проверяется здесь, на итоговой сетке,
            // а не в каждом этапе по отдельности.
            int crossingsTotal = 0, crossingsLeft = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                innerSegments = SplitSegmentsAtIntersections(innerSegments, out crossingsLeft);
                if (crossingsLeft == 0) break;

                crossingsTotal += crossingsLeft;
                // Точка пересечения стала узлом — она может лежать и на третьем ребре.
                innerSegments = SplitSegmentsAtNodes(innerSegments, cellSize, out _, out int splitDupsC);
                splitDupsTotal += splitDupsC;
                innerSegments = DeduplicateSegments(innerSegments);
            }
            res.CrossingsLeft = crossingsLeft;
            if (crossingsTotal > 0)
                res.Log.Add($"\nПересечений линий без узла: {crossingsTotal} — в каждое врезан узел.\n");
            if (crossingsLeft > 0)
                res.Log.Add($"ВНИМАНИЕ: пересечений без узла осталось: {crossingsLeft} — сообщите об этом, это ошибка плагина.\n");

            // Постусловия: то, что конвейер обязан был обеспечить своими этапами,
            // проверяется числом, а не на глаз по чертежу (см. SelfCheck.cs).
            RunMeshSelfCheck(res, innerSegments, contourPts, voidPolys);

            res.Log.Add($"\nОтрезков всего: {allSegments.Count}, после удаления совпадающих: {uniqueSegments.Count}, удалено по внешнему контуру: {removedOnContour}, срезано по стенам: {removedOnWalls}, устранено наложений: {mergedOverlaps}, схлопнуто коротких рёбер: {weldedEdges}, связей углов пилонов: {cornerLinks}, разбито рёбер узлами: {splitEdges}, отброшено совпавших при разрезке: {splitDupsTotal}, замкнуто открытых узлов: {closedNodes}, итог: {innerSegments.Count}\n");

            // Маркировка проблем: центры нетриангулированных полигонов и открытые
            // узлы, которые не удалось замкнуть, — красные круги в слое проблем.
            // Страховка: точку строго внутри пустоты (проёма/пилона) НЕ помечаем —
            // сетки там и не должно быть, а «незамкнутость» фиксировалась ДО финальной
            // обрезки проёмов и оставляла осиротевшие круги в пустоте.
            var problemPts = new List<Point2d>();
            foreach (var p in failedPolygonPts)
                if (!PointInsideAnyVoid(p, voidPolys)) problemPts.Add(p);
            foreach (var p in unclosedNodes)
                if (!PointInsideAnyVoid(p, voidPolys)) problemPts.Add(p);
            // Узлы контура пилона отмечаются без фильтра по пустотам: они лежат на
            // грани отпечатка, а отпечаток — не пустота.
            problemPts.AddRange(unlinkedPylonNodes);
            if (problemPts.Count > 0)
                res.Log.Add($"\nВНИМАНИЕ: проблемных мест сетки: {problemPts.Count} (не разбитых полигонов: {failedPolygonPts.Count}, незамкнутых узлов: {unclosedNodes.Count}, узлов контура пилонов вне сетки: {unlinkedPylonNodes.Count}) — отмечены кругами в слое {ProblemLayerName}. Поправьте расположение объектов в этих местах и перестройте сетку.\n");

            res.Segments = innerSegments;
            res.ProblemPts = problemPts;
            res.Ok = true;
            return res;
        }
    }
}
