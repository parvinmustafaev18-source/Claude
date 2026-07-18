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
                var mH = System.Text.RegularExpressions.Regex.Match(
                    slabEnt != null ? slabEnt.Layer : "", @"FOUNDATION_SLABS\(H-([\d.,]+)\)");
                if (mH.Success)
                    thicknessMm = double.Parse(mH.Groups[1].Value.Replace(',', '.'),
                        System.Globalization.CultureInfo.InvariantCulture);
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
                if (!ValidateContour(pline, ed, tr, db, out var contourPts)) return;
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

                // Стены: исходные отрезки + толщина из имени слоя WALLS(H-..)
                var wallOrig = new List<Point2d[]>();
                var wallOrigThickness = new List<double>();

                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || string.IsNullOrEmpty(ent.Layer)) continue;

                    if (ent is DBPoint dbp && IsColumnLayer(ent.Layer))
                    {
                        columnCenters.Add(new Point2d(dbp.Position.X, dbp.Position.Y));
                        var m = System.Text.RegularExpressions.Regex.Match(ent.Layer, @"B-([\d.,]+)\s+H-([\d.,]+)");
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

                    bool isWall = ent.Layer.StartsWith("WALLS(H-");
                    bool meshLayer = ent.Layer == TriangulationLayerName || isWall;
                    if (!meshLayer) continue;

                    double wallT = 200.0;
                    if (isWall)
                    {
                        var mw = System.Text.RegularExpressions.Regex.Match(ent.Layer, @"H-([\d.,]+)");
                        if (mw.Success) wallT = ParseNum(mw.Groups[1].Value);
                    }

                    if (ent is Line line)
                    {
                        var seg = new Point2d[]
                        {
                            new Point2d(line.StartPoint.X, line.StartPoint.Y),
                            new Point2d(line.EndPoint.X, line.EndPoint.Y)
                        };
                        segments.Add(seg);
                        if (isWall) { wallOrig.Add(seg); wallOrigThickness.Add(wallT); }
                    }
                    else if (ent is Polyline wp)
                    {
                        var verts = GetPolylineVertices(wp);
                        int segCount = wp.Closed ? verts.Count : verts.Count - 1;
                        for (int i = 0; i < segCount; i++)
                        {
                            var seg = new Point2d[] { verts[i], verts[(i + 1) % verts.Count] };
                            segments.Add(seg);
                            if (isWall) { wallOrig.Add(seg); wallOrigThickness.Add(wallT); }
                        }
                    }
                }

                segments = DeduplicateSegments(segments);
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

                // Глобальные 3D-узлы задачи (плита z=0, стены и пилоны растут вверх)
                var nodes3 = new List<double[]>();
                var node3Index = new Dictionary<string, int>();
                int Node3(double x, double y, double z)
                {
                    string key = Math.Round(x, 3) + "_" + Math.Round(y, 3) + "_" + Math.Round(z, 3);
                    int idx;
                    if (!node3Index.TryGetValue(key, out idx))
                    {
                        idx = nodes3.Count;
                        nodes3.Add(new double[] { x, y, z });
                        node3Index[key] = idx;
                    }
                    return idx;
                }
                int SlabNode(int i2d) { return Node3(nodes[i2d].X, nodes[i2d].Y, 0.0); }

                // Жёсткости: 1 — плита; далее стены по толщинам; далее сечения пилонов
                var wallStiffIds = new Dictionary<double, int>();
                var colStiffIds = new Dictionary<string, int>();
                var colStiffDims = new List<double[]>();
                int nextStiff = 2;

                // Элементы: {тип КЭ, № жёсткости, узлы...}
                var elements = new List<int[]>();
                int failedFaces = 0, fanFaces = 0;

                // Грани -> пластины плиты: 3 узла -> КЭ 42, 4 узла -> КЭ 44 (порядок узлов
                // КЭ 44 — "змейкой": p0 p1 p3 p2), больше 4 (висячие узлы) -> триангуляция.
                // Грань с центром пилона внутри разбивается веером треугольников вокруг
                // центра — центр становится узлом сетки, к нему цепляется стержень пилона.
                foreach (var face in faces)
                {
                    var poly = new List<Point2d>();
                    foreach (int idx in face) poly.Add(nodes[idx]);

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
                        elements.Add(new int[] { 44, 1, SlabNode(face[0]), SlabNode(face[1]), SlabNode(face[3]), SlabNode(face[2]) });
                    }
                    else
                    {
                        int failed = 0;
                        foreach (var t in TriangulateSimplePolygon(poly, ref failed))
                            elements.Add(new int[] { 42, 1, Node3(t[0].X, t[0].Y, 0), Node3(t[1].X, t[1].Y, 0), Node3(t[2].X, t[2].Y, 0) });
                        failedFaces += failed;
                    }
                }

                int slabElemCount = elements.Count;
                if (slabElemCount == 0)
                {
                    ed.WriteMessage("\nНе найдено ни одной замкнутой ячейки сетки — сначала постройте сетку (MESHQUADMESH).\n");
                    return;
                }

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
                int rows = zLevels.Count - 1;
                int wallElemCount = 0;

                foreach (var seg in segments)
                {
                    double thickness = -1;
                    for (int w = 0; w < wallOrig.Count; w++)
                    {
                        if (IsPointOnSegment(seg[0], wallOrig[w][0], wallOrig[w][1], 1e-3) &&
                            IsPointOnSegment(seg[1], wallOrig[w][0], wallOrig[w][1], 1e-3))
                        {
                            thickness = wallOrigThickness[w];
                            break;
                        }
                    }
                    if (thickness < 0) continue;

                    double tKey = Math.Round(thickness, 1);
                    if (!wallStiffIds.ContainsKey(tKey))
                        wallStiffIds[tKey] = nextStiff++;
                    int stiffId = wallStiffIds[tKey];

                    for (int k = 1; k <= rows; k++)
                    {
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
                sb.AppendLine("1 GEI " + elasticModulus.ToString("0.###e+000", inv) + " 0.2 "
                    + thicknessM.ToString("0.###", inv) + " RO 2.5 /");
                foreach (var kv in wallStiffIds)
                {
                    sb.AppendLine(kv.Value + " GEI " + elasticModulus.ToString("0.###e+000", inv) + " 0.2 "
                        + (kv.Key / 1000.0).ToString("0.###", inv) + " RO 2.5 /");
                }
                foreach (var cd in colStiffDims)
                {
                    sb.AppendLine((int)cd[2] + " S0 " + elasticModulus.ToString("0.###e+000", inv) + " "
                        + (cd[0] / 10.0).ToString("0.#", inv) + " " + (cd[1] / 10.0).ToString("0.#", inv) + "/");
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

                int quadCount = 0, triCount = 0;
                for (int i = 0; i < slabElemCount; i++)
                    if (elements[i][0] == 44) quadCount++; else triCount++;

                ed.WriteMessage($"\nЭкспортировано: узлов {nodes3.Count}; плита: КЭ 44 {quadCount}, КЭ 42 {triCount} (вееров под пилонами: {fanFaces}); стены: КЭ 44 {wallElemCount} (толщин: {wallStiffIds.Count}); пилоны: стержней КЭ 10 {barCount} (сечений: {colStiffIds.Count})" +
                    (failedFaces > 0 ? $"; потеряно граней: {failedFaces}" : "") +
                    (columnsWithoutDims > 0 ? $"; пилонов без размеров в имени слоя (принято 400x400): {columnsWithoutDims}" : "") + "\n");
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
