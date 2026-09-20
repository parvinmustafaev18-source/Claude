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
        [CommandMethod("LIREXPORT")]
        public void ExportTaskTextCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "LIREXPORT");
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
            // проставленного командой LIRLAYERS; ручной запрос — только если контур
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
                    "\nВ слое контура нет толщины (нужен FOUNDATION_SLABS(H-...), см. LIRLAYERS). Толщина плиты, мм: ");
                pdoH.DefaultValue = 300.0;
                pdoH.AllowNegative = false;
                pdoH.AllowZero = false;
                PromptDoubleResult pdrH = ed.GetDouble(pdoH);
                if (pdrH.Status != PromptStatus.OK) return;
                thicknessMm = pdrH.Value;
            }

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
                // лежат в сетке (LIRBUILD отпечатывает контур), а лишние рёбра дали
                // бы наложение. Нужны только для того, чтобы отличить элементы плиты,
                // попавшие в тело пилона, и дать им свою жёсткость.
                var pylonRects = GetPylonOutlines(tr, db, out _, out _);

                // ---- РАСЧЁТ -------------------------------------------------------
                // Всё, что дальше, считает ядро без чертежа (ExportCore.cs): планарный
                // граф, элементы, жёсткости, выдавливание стен и текст задачи.
                string dwgPath = db.Filename;
                string baseDir = (string.IsNullOrEmpty(dwgPath) || dwgPath.StartsWith("."))
                    ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments)
                    : System.IO.Path.GetDirectoryName(dwgPath);
                string taskName = ((string.IsNullOrEmpty(dwgPath) || dwgPath.StartsWith("."))
                    ? "MESHPLUGIN"
                    : System.IO.Path.GetFileNameWithoutExtension(dwgPath).ToUpperInvariant()) + "_LIRA";

                var exportInput = new ExportInput
                {
                    Contour = contourPts,
                    Segments = segments,
                    HolePolys = holePolys,
                    PylonRects = pylonRects,
                    WallOrig = wallOrig,
                    WallThickness = wallOrigThickness,
                    WallIsPylon = wallOrigIsPylon,
                    WallSizeKey = wallOrigSizeKey,
                    DoorSegs = doorOrig,
                    DoorHeights = doorHeights,
                    ColumnCenters = columnCenters,
                    ColumnDims = columnDims,
                    ColumnsWithoutDims = columnsWithoutDims,
                    ThicknessMm = thicknessMm,
                    ElasticModulus = elasticModulus,
                    UnitWeight = unitWeight,
                    FloorHeight = floorHeight,
                    WallStep = wallStep,
                    PylonStiffFactor = pylonStiffFactor,
                    TaskName = taskName
                };

                var exportWatch = System.Diagnostics.Stopwatch.StartNew();
                var task = BuildExportCore(exportInput);
                exportWatch.Stop();
                foreach (var line in task.Log) ed.WriteMessage(line);

                if (!task.Ok)
                {
                    ed.WriteMessage(task.Error);
                    return;
                }

                // ---- ЗАПИСЬ ФАЙЛОВ ------------------------------------------------
                // Кодировка 1251 — как в файлах, которые пишет сама ЛИРА. Имя задачи в
                // документе 0 обязано совпадать с именем файла, иначе ЛИРА переименует
                // задачу и предупредит об этом.
                string planDir = System.IO.Path.Combine(baseDir, "LIRA_PLANS");
                System.IO.Directory.CreateDirectory(planDir);
                string outPath = System.IO.Path.Combine(planDir, taskName + ".txt");
                string legendPath = System.IO.Path.Combine(planDir, taskName + "_LEGEND.txt");
                System.IO.File.WriteAllText(outPath, task.TaskText, System.Text.Encoding.GetEncoding(1251));
                System.IO.File.WriteAllText(legendPath, task.LegendText, System.Text.Encoding.GetEncoding(1251));

                if (task.LostFacePts.Count > 0)
                    DrawMarkCircles(tr, db, ProblemLayerName, task.LostFacePts, ProblemMarkRadius);

                ed.WriteMessage($"Легенда: {legendPath}\n");
                ed.WriteMessage($"Расчёт экспорта: {exportWatch.Elapsed.TotalSeconds:0.0} с\n");
                ed.WriteMessage($"Файл: {outPath}\n");
                ed.WriteMessage("Импорт в ЛИРЕ: Файл → Импортировать задачу → тип \"Текстовые файлы (*.txt)\". После импорта рекомендуется Упаковка схемы.\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка LIREXPORT: {ex.Message}\n");
            }
        }

    }
}
