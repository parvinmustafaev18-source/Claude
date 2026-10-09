using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    // ПЕРЕСТРОЕНИЕ СЕТКИ В ЛОКАЛЬНОЙ ОБЛАСТИ (LIRREMESH).
    //
    // Зачем. Сетка на плане построена и местами поправлена руками, но один
    // участок надо перебить заново — другим шагом или просто начисто. Полный
    // LIRBUILD для этого не годится: он строит ВСЮ сетку и выбрасывает все
    // ручные правки. Эта команда трогает только указанную область.
    //
    // Главное требование — СТЫК. Новая сетка обязана сойтись с окружающей узел
    // в узел: в ЛИРЕ связь между элементами идёт только через общие узлы, линия,
    // упёршаяся в чужое ребро без узла, не держит ничего. Стык собирается из
    // того, что в плагине уже есть:
    //   * узлы окружающей сетки на границе области («якоря») идут МЯГКИМИ целями
    //     выравнивания — линия новой сетки, если она рядом, садится ровно на
    //     якорь и продолжает наружную линию насквозь;
    //   * SplitSegmentsAtNodes врезает каждый якорь в ребро новой сетки (и
    //     наоборот), поэтому висячих узлов на границе не остаётся;
    //   * SplitSegmentsAtIntersections закрывает жёсткое правило 6;
    //   * якорь, которому новая линия не нашлась, остаётся Т-узлом — при
    //     экспорте грань с лишним узлом триангулируется (ExportCore: грань
    //     больше 4 узлов → разбиение), то есть соседний элемент уходит в ЛИРУ
    //     веером треугольников. Это штатный переход, а не дефект.
    public partial class Commands
    {
        [CommandMethod("LIRREMESH")]
        public void LocalMeshCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "LIRREMESH");
            Database db = doc.Database;

            PromptEntityOptions peo = new PromptEntityOptions(
                "\nВыберите замкнутый контур области, где перестроить сетку (полилинию): ");
            peo.SetRejectMessage("\nМожно выбрать только полилинию (LWPOLYLINE).");
            peo.AddAllowedClass(typeof(Polyline), false);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            PromptDoubleOptions pdoSize = new PromptDoubleOptions("\nРазмер стороны элемента в области: ");
            pdoSize.DefaultValue = 300.0;
            pdoSize.AllowNegative = false;
            pdoSize.AllowZero = false;
            pdoSize.Keywords.Add("300");
            pdoSize.Keywords.Add("400");
            pdoSize.Keywords.Add("500");
            pdoSize.AppendKeywordsToMessage = true;
            PromptDoubleResult pdrSize = ed.GetDouble(pdoSize);
            double cellSize;
            if (pdrSize.Status == PromptStatus.Keyword) cellSize = double.Parse(pdrSize.StringResult);
            else if (pdrSize.Status == PromptStatus.OK) cellSize = pdrSize.Value;
            else { ed.WriteMessage("\nШаг не задан.\n"); return; }

            // Точка возврата: команда стирает кусок готовой сетки, в которой обычно
            // уже есть ручные правки. Та же логика, что в LIRBUILD.
            SaveDrawingBeforeWork(doc, ed);

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EraseMarksOnLayer(tr, db, ProblemLayerName);
                WarnBadPolylines(tr, db, ed);

                Polyline pl = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Polyline;
                if (pl == null) { ed.WriteMessage("\nНужна полилиния.\n"); return; }

                // Контур области проверяется так же, как контур плиты: замкнут, без
                // дуг, без самопересечений. Отказ коммитится — маркеры должны остаться.
                List<Point2d> region;
                if (!ValidateContour(pl, ed, tr, db, out region)) { tr.Commit(); return; }
                EnsureCcw(region);

                // Жёсткое правило 1 остаётся правилом: сетка не выходит за плиту,
                // значит и область не вправе.
                var slab = GetSlabContour(tr, db);
                if (slab != null && !IsPolygonInsideContour(region, slab))
                {
                    DrawProblemMarks(tr, db, ProblemMark.From(
                        new List<Point2d> { PolygonCentroid(region) }, "область вне плиты"));
                    ed.WriteMessage($"\nОшибка: контур области выходит за контур плиты. Сетка не тронута, место отмечено кругом в слое {ProblemLayerName}.\n");
                    tr.Commit();
                    return;
                }
                if (slab == null)
                    ed.WriteMessage($"\nВНИМАНИЕ: контур плиты (слой {SlabLayerPrefix}H-...)) не найден — проверка «область внутри плиты» пропущена.\n");

                // ---- ЧТЕНИЕ СЕТКИ --------------------------------------------------
                var meshEnts = new List<ObjectId>();
                var meshSegs = new List<Point2d[]>();
                var meshOwner = new List<int>();
                ReadMeshSegments(tr, db, meshEnts, meshSegs, meshOwner,
                    new List<ObjectId> { per.ObjectId });
                ed.WriteMessage($"Линий сетки прочитано из {TriangulationLayerName}: {meshSegs.Count} (объектов: {meshEnts.Count})\n");

                // ГРАНИЦА ОБЛАСТИ ПЕРЕСТАВЛЯЕТСЯ НА ЛИНИИ СУЩЕСТВУЮЩЕЙ СЕТКИ, внутрь.
                //
                // Нарисованный от руки контур почти всегда режет ячейки где попало:
                // его грань лежит между линиями сетки, и узлы окружающей сетки рядом
                // с ней поставить линию уже не дают (BuildGridCoords бережёт просвет),
                // отчего на границе родятся Т-стыки. Если же граница совпадает с
                // ЛИНИЕЙ сетки, узлы на ней — обычные узлы сетки, и новая сетка
                // смыкается со старой без единого Т-узла.
                //
                // Поэтому каждая прямая грань контура двигается внутрь до ближайшей
                // линии сетки, идущей вдоль неё. Ряд элементов между нарисованным
                // контуром и новой границей остаётся нетронутым — он и служит стыком
                // (предложение пользователя 07.10.2026: «обведу с запасом в один ряд,
                // а программа пусть удаляет после первого ряда»).
                int shrunkEdges;
                var snapped = ShrinkRegionToMeshLines(region, meshSegs, out shrunkEdges);
                if (snapped != null && shrunkEdges > 0)
                {
                    region = snapped;
                    ed.WriteMessage($"Граница области переставлена на линии существующей сетки внутрь: граней {shrunkEdges}. Ряд элементов между вашим контуром и новой границей остаётся нетронутым — он и есть стык.\n");
                }
                else
                {
                    ed.WriteMessage("Границу области переставить на линии сетки не удалось (контур не прямоугольный, линий вдоль граней нет или после сдвига область вырождается) — работаем по нарисованному контуру.\n");
                }

                // Всё, что внутри области, уходит: куски рёбер внутри отрезаются,
                // целиком внутренние пропадают. Делать это руками инженеру не нужно —
                // достаточно обвести область.
                var regionAsPolys = new List<List<Point2d>> { region };
                int clippedAtRegion, removedInRegion;
                var outer = ClipSegmentsOutsideColumns(meshSegs, regionAsPolys, out clippedAtRegion, out removedInRegion);
                ed.WriteMessage($"Старая сетка в области: под снос отрезков {removedInRegion}, под подрезку по границе {clippedAtRegion}\n");

                // ---- ЯКОРЯ ---------------------------------------------------------
                // Узлы окружающей сетки, сидящие на границе области. К ним новая сетка
                // обязана пристыковаться; их координаты идут мягкими целями, чтобы
                // линия новой сетки продолжала наружную, а не шла рядом с ней.
                var anchors = new List<Point2d>();
                var anchorIndex = new NodeIndex();
                foreach (var s in outer)
                {
                    for (int e = 0; e < 2; e++)
                    {
                        if (!IsOnPolygonBoundary(s[e], region, MeshTol.Collinear)) continue;
                        int before = anchorIndex.Nodes.Count;
                        anchorIndex.GetNode(s[e]);
                        if (anchorIndex.Nodes.Count > before) anchors.Add(s[e]);
                    }
                }

                var anchorXs = new List<double>();
                var anchorYs = new List<double>();
                int rn = region.Count;
                foreach (var a in anchors)
                {
                    for (int i = 0; i < rn; i++)
                    {
                        Point2d p = region[i], q = region[(i + 1) % rn];
                        if (!IsPointOnSegment(a, p, q, MeshTol.Collinear)) continue;
                        // Якорь на вертикальной грани задаёт Y для горизонтальной линии
                        // сетки, на горизонтальной — X для вертикальной. У наклонной
                        // грани координаты нет: там стык даст Т-узлы и треугольники.
                        if (Math.Abs(p.X - q.X) < MeshTol.Collinear) anchorYs.Add(a.Y);
                        else if (Math.Abs(p.Y - q.Y) < MeshTol.Collinear) anchorXs.Add(a.X);
                        break;
                    }
                }
                ed.WriteMessage($"Узлов окружающей сетки на границе области: {anchors.Count}; из них задают линию новой сетки: {anchorXs.Count + anchorYs.Count} (остальные на наклонных гранях — там стык даст Т-узлы)\n");

                // ---- ВХОД ЯДРА, ОБРЕЗАННЫЙ ОБЛАСТЬЮ --------------------------------
                // Ядро проверяет жёсткие правила ОТНОСИТЕЛЬНО переданного контура:
                // стена или отверстие снаружи него — нарушение и остановка. Поэтому
                // всё, что приходит из чертежа, обрезается областью. Стены режутся
                // (кусок внутри остаётся), а отверстие и пилон разрезать нельзя —
                // область, рассекающая их контур, это ошибка инженера, и команда
                // говорит об этом прямо.
                var allWalls = GetWallSegments(tr, db);
                if (allWalls.Count > WallSegmentsSanityLimit)
                {
                    ed.WriteMessage(
                        $"\n[СТОП] Сегментов стен {allWalls.Count} при разумном пределе {WallSegmentsSanityLimit}.\n" +
                        $"Почти наверняка на слое стен лежит сетка прошлого построения — см. LIRBUILD.\nКоманда остановлена, чертёж не изменён.\n");
                    return;
                }

                var cutMarks = new List<ProblemMark>();
                var wallsIn = ClipSegmentsToContour(allWalls, region, out _, out _);
                var fixedIn = ClipSegmentsToContour(GetWallSegments(tr, db, true), region, out _, out _);
                var crossesIn = ClipSegmentsToContour(GetPylonCrossConstraints(tr, db), region, out _, out _);

                // Контур, рассечённый границей области, обрезается по ней: ядру нужна
                // пустота, целиком лежащая внутри переданного контура, а то, что
                // осталось снаружи, и так представлено нетронутой сеткой. Обрезка идёт
                // Сазерлендом–Ходжманом, а он верен только для ВЫПУКЛОЙ области —
                // поэтому выпуклость проверяется, и на вогнутой области рассечённый
                // контур по-прежнему отказ.
                bool convex = IsConvexPolygon(region);
                int clippedHoles = 0, clippedColumns = 0, clippedRects = 0;

                var columnsIn = ClipPolysToRegion(GetColumnPolygons(tr, db), region, convex, false,
                    cutMarks, "область режет пилон", ref clippedColumns);
                var holesIn = ClipPolysToRegion(GetHolePolygons(tr, db), region, convex, false,
                    cutMarks, "область режет отверстие", ref clippedHoles);
                // Отпечаток пилона ядро описывает ПРЯМОУГОЛЬНИКОМ (мелкая сетка внутри
                // строится по габаритам). Обрезанный угол перестал бы им быть, поэтому
                // от обрезанного отпечатка требуется остаться прямоугольником.
                var pylonsIn = ClipPolysToRegion(GetPylonOutlines(tr, db, out _, out _), region, convex, true,
                    cutMarks, "область режет отпечаток пилона", ref clippedRects);

                if (clippedHoles + clippedColumns + clippedRects > 0)
                    ed.WriteMessage($"Обрезано границей области: отверстий {clippedHoles}, пилонов {clippedColumns}, отпечатков {clippedRects} — внутри области считается только попавшая в неё часть\n");
                // Обрезанный отпечаток — единственный случай, где обрезка реально
                // портит сетку: ядро обязано поставить узел на каждую грань
                // отпечатка, а срезанная грань лежит на границе области, и рядом с
                // ней родятся рёбра в десятки миллиметров. Пилон лучше обводить
                // целиком — об этом и предупреждаем, а не молчим.
                if (clippedRects > 0)
                    ed.WriteMessage($"ВНИМАНИЕ: граница области режет отпечаток пилона ({clippedRects} шт.). У такого пилона в области возможны короткие рёбра и узлы контура вне сетки. Лучше обвести область так, чтобы пилон попадал в неё целиком.\n");

                if (cutMarks.Count > 0)
                {
                    DrawProblemMarks(tr, db, cutMarks);
                    var where = new List<string>();
                    foreach (var m in cutMarks) where.Add($"({m.Pt.X:0}, {m.Pt.Y:0})");
                    ed.WriteMessage($"\nОшибка: граница области рассекает контуры, которые разрезать нечем: {cutMarks.Count} шт., центры {string.Join(", ", where)}.\n" +
                        (convex
                            ? "Это отпечаток пилона: его внутренняя сетка строится по габаритному прямоугольнику, и срезанный угол им уже не описать.\n"
                            : "Контур области ВОГНУТЫЙ, а обрезка отверстий и пилонов верна только для выпуклой области.\n") +
                        $"Что сделать: сдвинуть границу области так, чтобы эти контуры попадали в неё целиком или не попадали вовсе" +
                        (convex ? "" : ", либо обвести область выпуклым контуром (прямоугольником)") +
                        $". Сетка не тронута, места отмечены кругами в слое {ProblemLayerName}.\n");
                    tr.Commit();
                    return;
                }

                // Участки другой толщины (MESH_THICK). Их кромки уже лежат в сетке —
                // их совместил с сеткой инженер, — и перестроение сотрёт их вместе со
                // старой сеткой. Поэтому они собираются здесь заново: прямые участки
                // идут ЖЁСТКИМИ целями (линия сетки садится ровно на кромку), а сами
                // кромки ниже добавляются рёбрами. Иначе после LIRREMESH граница
                // участка внутри области пропадала бы, и экспорт раздавал бы толщину
                // по центрам элементов, которые её пересекают.
                int thickOpen, thickNoT;
                var zones = GetThickZones(tr, db, out thickOpen, out thickNoT);
                var zoneEdgesIn = new List<Point2d[]>();
                foreach (var z in zones)
                {
                    int zn = z.Poly.Count;
                    var edges = new List<Point2d[]>();
                    for (int i = 0; i < zn; i++)
                        edges.Add(new Point2d[] { z.Poly[i], z.Poly[(i + 1) % zn] });
                    zoneEdgesIn.AddRange(ClipSegmentsToContour(edges, region, out _, out _));
                }
                if (zones.Count > 0)
                    ed.WriteMessage($"Участков другой толщины ({ThickLayerPrefix}H-...)): {zones.Count}, их кромок внутри области: {zoneEdgesIn.Count}\n");

                var doorEndsIn = new List<Point2d>();
                foreach (var p in GetDoorEndpoints(tr, db))
                    if (IsPointInPolygon(p, region) || IsOnPolygonBoundary(p, region, MeshTol.Collinear))
                        doorEndsIn.Add(p);

                // Мягкие цели выравнивания из чертежа — только те, что попали в габарит
                // области: цель снаружи только сдвинула бы линию без пользы.
                var bb = PolygonBBox(region);
                var jambXs = new List<double>(); var jambYs = new List<double>();
                GetDoorJambConstraints(tr, db, cellSize, jambXs, jambYs);
                var axisXs = new List<double>(); var axisYs = new List<double>();
                GetPylonAxisTargets(tr, db, axisXs, axisYs);
                jambXs = KeepInRange(jambXs, bb[0], bb[2]); jambYs = KeepInRange(jambYs, bb[1], bb[3]);
                axisXs = KeepInRange(axisXs, bb[0], bb[2]); axisYs = KeepInRange(axisYs, bb[1], bb[3]);

                // Якоря — ЖЁСТКИЕ цели: на каждом узле окружающей сетки линия новой
                // сетки обязана стоять, тогда сетки смыкаются узел в узел и новая
                // продолжает старую. Сначала они были мягкими (двигают линию, только
                // если она ближе 100 мм) — на реальном плане 07.10.2026 это дало ровно
                // то, на что пожаловался пользователь: «новая локальная сетка не видит
                // сетку вокруг». Да, каждый якорь ставит линию через всю область, и
                // заданный шаг работает только там, где окружающая сетка реже, — это
                // и есть цена связности, она важнее.
                var hardXs = new List<double>(anchorXs);
                var hardYs = new List<double>(anchorYs);

                var input = new MeshInput
                {
                    Contour = region,
                    CellSize = cellSize,
                    WallSegments = wallsIn,
                    DoorEnds = doorEndsIn,
                    ColumnPolys = columnsIn,
                    HolePolys = holesIn,
                    PylonRects = pylonsIn,
                    PylonCrosses = crossesIn,
                    FixedWallSegments = fixedIn,
                    HardTargetSegments = zoneEdgesIn,
                    JambXs = jambXs,
                    JambYs = jambYs,
                    AxisXs = axisXs,
                    AxisYs = axisYs,
                    HardXs = hardXs,
                    HardYs = hardYs,

                    // Двери НЕ подтягиваются: их уже поставило основное построение, и
                    // двигать их второй раз значит рассогласовать с остальным планом.
                    // Но поперечные разрезы через косяки нужны и здесь — иначе косяк
                    // двери внутри области останется без узла.
                    SnapDoors = (xs, ys, log) =>
                    {
                        var jambs = GetDoorJambConstraints(tr, db, cellSize, new List<double>(), new List<double>());
                        var inside = new List<Point2d[]>();
                        foreach (var j in jambs)
                        {
                            Point2d mid = new Point2d((j[0].X + j[1].X) / 2.0, (j[0].Y + j[1].Y) / 2.0);
                            if (IsPointInPolygon(mid, region)) inside.Add(j);
                        }
                        return inside;
                    }
                };

                // ---- РАСЧЁТ --------------------------------------------------------
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var mesh = BuildMeshCore(input);
                watch.Stop();
                foreach (var line in mesh.Log) ed.WriteMessage(line);
                ed.WriteMessage($"\nРасчёт сетки в области: {watch.Elapsed.TotalSeconds:0.0} с\n");

                if (!mesh.Ok)
                {
                    // Нарушено жёсткое правило — чертёж не меняем вовсе, маркеры своей
                    // транзакцией (как в LIRBUILD).
                    tr.Abort();
                    MarkProblemPoints(db, mesh.ErrorPts);
                    return;
                }

                // ---- СБОРКА И СТЫК -------------------------------------------------
                // Грани самой области обязаны стать рёбрами сетки: ячейки изнутри и
                // снаружи закрываются именно ими. Совпавшие с уже существующим ребром
                // куски схлопнет разрез по узлам — он выпускает каждое ребро один раз.
                // Врезка идёт ТОЛЬКО по полосе вокруг области: разрез по узлам,
                // пущенный по всему плану, подровнял бы и чужие Т-узлы далеко от
                // области — а это ручные правки инженера, которые команда трогать
                // не должна. Полоса — габарит области плюс шаг: любой отрезок,
                // у которого есть узел на границе области, в неё попадает.
                double mx0 = bb[0] - cellSize - 1.0, my0 = bb[1] - cellSize - 1.0;
                double mx1 = bb[2] + cellSize + 1.0, my1 = bb[3] + cellSize + 1.0;
                bool NearRegion(Point2d a, Point2d b)
                {
                    return Math.Max(a.X, b.X) >= mx0 && Math.Min(a.X, b.X) <= mx1
                        && Math.Max(a.Y, b.Y) >= my0 && Math.Min(a.Y, b.Y) <= my1;
                }

                var passive = new List<Point2d[]>();
                var work = new List<Point2d[]>();
                foreach (var s in outer)
                {
                    if (NearRegion(s[0], s[1])) work.Add(s);
                    else passive.Add(s);
                }
                work.AddRange(mesh.Segments);
                for (int i = 0; i < rn; i++)
                {
                    Point2d a = region[i], b = region[(i + 1) % rn];
                    if (a.GetDistanceTo(b) >= MeshTol.MinPiece) work.Add(new Point2d[] { a, b });
                }

                // Кромки участков другой толщины — ребром новой сетки.
                // Наклонная кромка цели не даёт, но ребром стать обязана всё равно.
                foreach (var e in zoneEdgesIn)
                    if (e[0].GetDistanceTo(e[1]) >= MeshTol.MinPiece) work.Add(e);

                int splitCount, dropped;
                work = SplitSegmentsAtNodes(work, cellSize, out splitCount, out dropped);
                int crossings;
                work = SplitSegmentsAtIntersections(work, out crossings);
                int crossingsLeft;
                var recheck = SplitSegmentsAtIntersections(work, out crossingsLeft);
                if (crossingsLeft > 0) work = recheck;

                var all = new List<Point2d[]>(passive.Count + work.Count);
                all.AddRange(passive);
                all.AddRange(work);

                // Сколько якорей новая сетка приняла линией насквозь, а сколько
                // осталось Т-узлами (их соседняя грань уйдёт в ЛИРУ треугольниками).
                var ibSeen = new NodeIndex();
                var ibNodes = new List<Point2d>();
                foreach (var s in mesh.Segments)
                    for (int e = 0; e < 2; e++)
                    {
                        if (!IsOnPolygonBoundary(s[e], region, MeshTol.Collinear)) continue;
                        int before = ibSeen.Nodes.Count;
                        ibSeen.GetNode(s[e]);
                        if (ibSeen.Nodes.Count > before) ibNodes.Add(s[e]);
                    }

                var ibGrid = new SpatialGrid(Math.Max(cellSize, 1.0));
                for (int i = 0; i < ibNodes.Count; i++) ibGrid.Add(i, ibNodes[i]);

                // Для несвязанного якоря важно не только «не связан», но и НАСКОЛЬКО
                // промахнулись: доли миллиметра — это рассогласование координат
                // (узлы почти совпали, но не слились), сотни миллиметров — линии
                // новой сетки там просто нет. Диагноз разный, поэтому печатаем разброс.
                int tied = 0;
                var untied = new List<Point2d>();
                double nearMin = double.MaxValue, nearMax = 0.0;
                foreach (var a in anchors)
                {
                    double near = double.MaxValue;
                    foreach (int i in ibGrid.QueryRadius(a, cellSize))
                    {
                        double d = a.GetDistanceTo(ibNodes[i]);
                        if (d < near) near = d;
                    }
                    if (near < MeshTol.NodeMerge) { tied++; continue; }
                    untied.Add(a);
                    if (near < nearMin) nearMin = near;
                    if (near > nearMax) nearMax = near;
                }

                ed.WriteMessage($"Стык: узлов окружающей сетки на границе {anchors.Count}, линия новой сетки продолжает {tied}" +
                    (anchors.Count - tied > 0 ? $", остальные {anchors.Count - tied} стали Т-узлами (связь полная: соседний элемент уйдёт в ЛИРУ разрезанным, но линия сквозь границу не идёт)" : " — все") + "\n");
                if (untied.Count > 0)
                {
                    var where = new List<string>();
                    for (int i = 0; i < untied.Count && i < 8; i++) where.Add($"({untied[i].X:0}, {untied[i].Y:0})");
                    ed.WriteMessage($"  Т-узлы: {string.Join(", ", where)}" + (untied.Count > 8 ? $" и ещё {untied.Count - 8}" : "") + "\n");
                    ed.WriteMessage($"  Ближайший узел новой сетки к такому якорю: от {(nearMin < double.MaxValue ? nearMin : 0):0.###} до {nearMax:0.#} мм. Доли миллиметра — значит узлы почти совпали и разошлись на допуске; сотни миллиметров — линии новой сетки там нет (цель отвергнута краем области).\n");
                }
                // КОНТРОЛЬ СВЯЗНОСТИ. Узел, у которого ровно одно ребро, — оборванный
                // конец: в ЛИРЕ он ничего не держит.
                //
                // Степень считается по ВСЕЙ сетке (all), а не по рабочей полосе. По
                // полосе считать нельзя: у каждой линии, пересекающей её край,
                // продолжение лежит в passive, и такой узел выглядит оборванным.
                // На реальном плане 07.10.2026 это дало 77 ложных тревог — ровно
                // столько линий пересекало край полосы.
                // Сообщаем только о том, что внутри полосы: чужие обрывы за её
                // пределами — следы прошлых ручных правок, и не наше дело.
                var degIndex = new NodeIndex();
                var degree = new List<int>();
                foreach (var s2 in all)
                {
                    for (int e = 0; e < 2; e++)
                    {
                        int ni2 = degIndex.GetNode(s2[e]);
                        while (degree.Count <= ni2) degree.Add(0);
                        degree[ni2]++;
                    }
                }
                var openPts = new List<Point2d>();
                for (int i = 0; i < degree.Count; i++)
                {
                    if (degree[i] != 1) continue;
                    Point2d p = degIndex.Nodes[i];
                    if (p.X < mx0 || p.X > mx1 || p.Y < my0 || p.Y > my1) continue;
                    openPts.Add(p);
                }

                ed.WriteMessage($"Врезка: рёбер разрезано узлом {splitCount}, узлов в пересечения {crossings}" +
                    (dropped > 0 ? $", совпавших рёбер отброшено {dropped}" : "") + "\n");
                if (crossingsLeft > 0)
                    ed.WriteMessage($"ВНИМАНИЕ: пересечений линий без узла осталось: {crossingsLeft} (жёсткое правило 6).\n");

                // ---- ЗАПИСЬ --------------------------------------------------------
                WarnLayerHidden(tr, db, ed, TriangulationLayerName);
                int erased, added, kept;
                ApplyMeshSegments(tr, db, meshEnts, meshSegs, meshOwner, all, out erased, out added, out kept);

                var marks = new List<ProblemMark>(mesh.ProblemPts);
                marks.AddRange(ProblemMark.From(openPts, "узел не связан"));
                if (marks.Count > 0) DrawProblemMarks(tr, db, marks);
                if (openPts.Count > 0)
                    ed.WriteMessage($"ВНИМАНИЕ: узлов с единственным ребром (оборванный конец, в ЛИРЕ ничего не держит): {openPts.Count} — отмечены кругами в слое {ProblemLayerName}.\n");
                else
                    ed.WriteMessage("Связность: оборванных узлов в полосе нет.\n");

                ed.WriteMessage($"Чертёж: оставлено без изменений отрезков {kept}, перерисовано {added}, удалено объектов {erased}\n");
                if (zoneEdgesIn.Count > 0)
                    ed.WriteMessage($"Кромки участков другой толщины внутри области восстановлены — повторять LIRTHICK не нужно.\n");
                ed.WriteMessage($"Контур области остался в чертеже — его можно стереть, на экспорт он не влияет.\n");
                ed.WriteMessage($"ВНИМАНИЕ: повторный LIRBUILD строит сетку заново и эту правку (вместе с ручными) потеряет.\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка LIRREMESH: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // Переставить каждую прямую грань контура области ВНУТРЬ, на ближайшую
        // линию существующей сетки, идущую вдоль этой грани.
        //
        // Линией считается не отдельный отрезок, а координата, вдоль которой
        // отрезки покрывают не меньше половины длины грани: сетка нарезана по
        // ячейкам, и один отрезок длиной в шаг линией не является.
        //
        // Работает только для контура из прямых горизонтальных и вертикальных
        // граней (прямоугольник, Г- и П-образные). У наклонной грани линии вдоль
        // неё нет — такой контур возвращается как есть (null).
        private List<Point2d> ShrinkRegionToMeshLines(
            List<Point2d> region, List<Point2d[]> mesh, out int movedEdges)
        {
            movedEdges = 0;
            int n = region.Count;
            if (n < 4) return null;

            // Грани: вид, координата, диапазон вдоль грани, направление внутрь.
            // Контур обойдён против часовой, значит внутренность — слева по ходу:
            // нормаль внутрь = поворот направления на +90 градусов.
            var kind = new char[n];
            var coord = new double[n];
            var lo = new double[n];
            var hi = new double[n];
            var dir = new int[n];

            for (int i = 0; i < n; i++)
            {
                Point2d p = region[i], q = region[(i + 1) % n];
                if (Math.Abs(p.X - q.X) < MeshTol.Collinear)
                {
                    kind[i] = 'V'; coord[i] = p.X;
                    lo[i] = Math.Min(p.Y, q.Y); hi[i] = Math.Max(p.Y, q.Y);
                    dir[i] = q.Y > p.Y ? -1 : 1;      // вверх -> внутрь по -X
                }
                else if (Math.Abs(p.Y - q.Y) < MeshTol.Collinear)
                {
                    kind[i] = 'H'; coord[i] = p.Y;
                    lo[i] = Math.Min(p.X, q.X); hi[i] = Math.Max(p.X, q.X);
                    dir[i] = q.X > p.X ? 1 : -1;      // вправо -> внутрь по +Y
                }
                else return null;                      // наклонная грань
                if (hi[i] - lo[i] < MeshTol.MinElementSize) return null;  // грань-огрызок
            }

            // Отрезки сетки по видам — один проход на весь план.
            var vx = new List<double[]>();   // {x, y0, y1}
            var hy = new List<double[]>();   // {y, x0, x1}
            foreach (var sg in mesh)
            {
                if (Math.Abs(sg[0].X - sg[1].X) < MeshTol.Collinear)
                    vx.Add(new double[] { sg[0].X, Math.Min(sg[0].Y, sg[1].Y), Math.Max(sg[0].Y, sg[1].Y) });
                else if (Math.Abs(sg[0].Y - sg[1].Y) < MeshTol.Collinear)
                    hy.Add(new double[] { sg[0].Y, Math.Min(sg[0].X, sg[1].X), Math.Max(sg[0].X, sg[1].X) });
            }

            var newCoord = new double[n];
            for (int i = 0; i < n; i++)
            {
                newCoord[i] = coord[i];
                var src = kind[i] == 'V' ? vx : hy;

                // Покрытие грани по каждой координате. Ключ (десятые доли мм) нужен
                // ТОЛЬКО чтобы сгруппировать отрезки одной линии; сама координата
                // берётся ТОЧНАЯ, как в чертеже.
                //
                // Это не придирка. Граница области становится линией сетки, и узлы
                // окружающей сетки обязаны лечь на неё РОВНО: NodeIndex сливает точки
                // с допуском 0.001 мм. Первая версия ставила границу в округлённые
                // 0.1 мм, и на реальном плане 07.10.2026 это развело почти все узлы
                // на сотые доли миллиметра — 44 Т-стыка из 79 на ровном месте.
                var cover = new Dictionary<long, double>();
                var exact = new Dictionary<long, double>();
                foreach (var L in src)
                {
                    double ov = Math.Min(hi[i], L[2]) - Math.Max(lo[i], L[1]);
                    if (ov <= 0) continue;
                    long key = (long)Math.Round(L[0] * 10.0);
                    double had;
                    cover[key] = (cover.TryGetValue(key, out had) ? had : 0.0) + ov;
                    if (!exact.ContainsKey(key)) exact[key] = L[0];
                }

                double need = 0.5 * (hi[i] - lo[i]);
                double bestDelta = double.MaxValue;
                foreach (var kv in cover)
                {
                    if (kv.Value < need) continue;
                    double c = exact[kv.Key];
                    double delta = (c - coord[i]) * dir[i];
                    if (delta < MeshTol.MinPiece) continue;        // не внутрь
                    if (delta < bestDelta) { bestDelta = delta; newCoord[i] = c; }
                }
                if (bestDelta < double.MaxValue) movedEdges++;
            }

            if (movedEdges == 0) return null;

            // Вершина i — пересечение грани i-1 и грани i. У прямоугольного контура
            // они всегда перпендикулярны, поэтому одна даёт X, другая Y.
            var res = new List<Point2d>(n);
            for (int i = 0; i < n; i++)
            {
                int pr = (i - 1 + n) % n;
                if (kind[i] == kind[pr]) return null;   // две одинаковые грани подряд
                res.Add(kind[i] == 'V'
                    ? new Point2d(newCoord[i], newCoord[pr])
                    : new Point2d(newCoord[pr], newCoord[i]));
            }

            // Проверки: область не должна вывернуться, схлопнуться или вылезти за
            // нарисованный контур. Не прошло — работаем по нарисованному.
            var clean = CleanupPolygon(res);
            if (clean.Count < 4) return null;
            EnsureCcw(clean);
            if (Math.Abs(PolygonArea(clean)) < 4.0 * MeshTol.MinElementSize * MeshTol.MinElementSize) return null;
            if (FindSelfIntersections(clean).Count > 0) return null;
            if (!IsPolygonInsideContour(clean, region)) return null;

            return clean;
        }

        // Контуры пустот (отверстия, пилоны, отпечатки), попавшие в область.
        // Целиком внутри — как есть. Рассечённый границей области ОБРЕЗАЕТСЯ по
        // ней: ядро требует, чтобы пустота целиком лежала внутри переданного
        // контура (иначе это «отверстие вне плиты» и остановка), а часть, оставшаяся
        // снаружи, и так представлена нетронутой сеткой. Обрезка — Сазерленд–Ходжман
        // по рёбрам области, она верна только для ВЫПУКЛОЙ области.
        // Контур, который обрезать нельзя (вогнутая область; отпечаток пилона, от
        // которого после среза не остаётся прямоугольника), уходит в cutMarks, и
        // команда отказывается работать.
        private List<List<Point2d>> ClipPolysToRegion(
            List<List<Point2d>> polys, List<Point2d> region, bool regionConvex, bool mustStayRect,
            List<ProblemMark> cutMarks, string cutText, ref int clippedCount)
        {
            var result = new List<List<Point2d>>();
            int rn = region.Count;

            foreach (var poly in polys)
            {
                if (IsPolygonInsideContour(poly, region)) { result.Add(poly); continue; }

                bool touches = false;
                foreach (var p in poly)
                    if (IsPointInPolygon(p, region)) { touches = true; break; }

                if (!touches)
                {
                    int pn = poly.Count;
                    for (int i = 0; i < pn && !touches; i++)
                        for (int j = 0; j < rn; j++)
                            if (SegmentsIntersect(poly[i], poly[(i + 1) % pn], region[j], region[(j + 1) % rn]))
                            { touches = true; break; }
                }
                if (!touches) continue;   // контур вне области — ядру он не нужен

                if (!regionConvex) { cutMarks.Add(new ProblemMark(PolygonCentroid(poly), cutText)); continue; }

                var clipped = CleanupPolygon(ClipPolygonToConvexRegion(poly, region));
                if (clipped.Count < 3 || Math.Abs(PolygonArea(clipped)) < MeshTol.MinArea)
                    continue;   // задел область только кромкой — считать нечего

                if (mustStayRect)
                {
                    var bb = PolygonBBox(clipped);
                    double b = bb[2] - bb[0], h = bb[3] - bb[1];
                    if (b < 1.0 || h < 1.0 ||
                        Math.Abs(Math.Abs(PolygonArea(clipped)) - b * h) > 0.05 * b * h)
                    {
                        cutMarks.Add(new ProblemMark(PolygonCentroid(poly), cutText));
                        continue;
                    }
                }

                EnsureCcw(clipped);
                result.Add(clipped);
                clippedCount++;
            }

            return result;
        }

        // Обрезка полигона по выпуклой области: та же схема Сазерленда–Ходжмана, что
        // у обрезки ячейки (ClipPolygonToConvexCell), только область — список вершин.
        private List<Point2d> ClipPolygonToConvexRegion(List<Point2d> subject, List<Point2d> region)
        {
            var result = new List<Point2d>(subject);
            int n = region.Count;
            for (int i = 0; i < n && result.Count > 0; i++)
                result = ClipPolygonAgainstEdge(result, region[i], region[(i + 1) % n]);
            return result;
        }

        // Выпуклость полигона, обойдённого против часовой: все повороты в одну сторону.
        // Нужна ровно затем, чтобы знать, можно ли обрезать по нему пустоты.
        private bool IsConvexPolygon(List<Point2d> poly)
        {
            int n = poly.Count;
            if (n < 3) return false;
            bool neg = false, pos = false;
            for (int i = 0; i < n; i++)
            {
                double cr = CrossProduct(poly[i], poly[(i + 1) % n], poly[(i + 2) % n]);
                if (cr < -MeshTol.MinArea) neg = true;
                else if (cr > MeshTol.MinArea) pos = true;
                if (neg && pos) return false;
            }
            return true;
        }

        // Координаты вне габарита области как цели выравнивания бесполезны: они
        // сдвинули бы линию, до которой им нет дела.
        private static List<double> KeepInRange(List<double> values, double lo, double hi)
        {
            var res = new List<double>();
            foreach (double v in values)
                if (v >= lo - MeshTol.Collinear && v <= hi + MeshTol.Collinear) res.Add(v);
            return res;
        }
    }
}
