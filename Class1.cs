using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace MeshPlugin
{
    public class Commands
    {
        [CommandMethod("MESHHELLO")]
        public void HelloCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            ed.WriteMessage("\nПривет! Плагин загружен и работает.\n");
        }

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

            PromptDoubleOptions pdoH = new PromptDoubleOptions("\nТолщина плиты, мм: ");
            pdoH.DefaultValue = 300.0;
            pdoH.AllowNegative = false;
            pdoH.AllowZero = false;
            PromptDoubleResult pdrH = ed.GetDouble(pdoH);
            if (pdrH.Status != PromptStatus.OK) return;
            double thicknessM = pdrH.Value / 1000.0;

            PromptDoubleOptions pdoE = new PromptDoubleOptions("\nМодуль упругости E, т/м² (B30 ≈ 3.31e6, B40 ≈ 3.67e6): ");
            pdoE.DefaultValue = 3.31e6;
            pdoE.AllowNegative = false;
            pdoE.AllowZero = false;
            PromptDoubleResult pdrE = ed.GetDouble(pdoE);
            if (pdrE.Status != PromptStatus.OK) return;
            double elasticModulus = pdrE.Value;

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
                if (!ValidateContour(pline, ed, out var contourPts)) return;
                EnsureCcw(contourPts);

                // Отрезки: контур плиты + линии сетки + стены. Центры пилонов запоминаем,
                // чтобы не превратить внутренность пилона в пластину.
                var segments = new System.Collections.Generic.List<Point2d[]>();
                int cn = contourPts.Count;
                for (int i = 0; i < cn; i++)
                    segments.Add(new Point2d[] { contourPts[i], contourPts[(i + 1) % cn] });

                double ParseNum(string s)
                {
                    return double.Parse(s.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
                }

                // Пилоны: центр + размеры сечения из имени слоя COLUMNS(SEC-RC_RECT B-.. H-..)
                var columnCenters = new System.Collections.Generic.List<Point2d>();
                var columnDims = new System.Collections.Generic.List<double[]>();
                int columnsWithoutDims = 0;

                // Стены: исходные отрезки + толщина из имени слоя WALLS(H-..)
                var wallOrig = new System.Collections.Generic.List<Point2d[]>();
                var wallOrigThickness = new System.Collections.Generic.List<double>();

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
                var nodes = new System.Collections.Generic.List<Point2d>();
                var nodeIndex = new System.Collections.Generic.Dictionary<string, int>();
                int GetNode(Point2d p)
                {
                    string key = System.Math.Round(p.X, 3) + "_" + System.Math.Round(p.Y, 3);
                    int idx;
                    if (!nodeIndex.TryGetValue(key, out idx))
                    {
                        idx = nodes.Count;
                        nodes.Add(p);
                        nodeIndex[key] = idx;
                    }
                    return idx;
                }

                var edges = new System.Collections.Generic.List<int[]>();
                foreach (var seg in segments)
                {
                    int ia = GetNode(seg[0]);
                    int ib = GetNode(seg[1]);
                    if (ia != ib) edges.Add(new int[] { ia, ib });
                }

                var faces = ExtractPlanarFaces(nodes, edges);

                // Глобальные 3D-узлы задачи (плита z=0, стены и пилоны растут вверх)
                var nodes3 = new System.Collections.Generic.List<double[]>();
                var node3Index = new System.Collections.Generic.Dictionary<string, int>();
                int Node3(double x, double y, double z)
                {
                    string key = System.Math.Round(x, 3) + "_" + System.Math.Round(y, 3) + "_" + System.Math.Round(z, 3);
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
                var wallStiffIds = new System.Collections.Generic.Dictionary<double, int>();
                var colStiffIds = new System.Collections.Generic.Dictionary<string, int>();
                var colStiffDims = new System.Collections.Generic.List<double[]>();
                int nextStiff = 2;

                // Элементы: {тип КЭ, № жёсткости, узлы...}
                var elements = new System.Collections.Generic.List<int[]>();
                int failedFaces = 0, fanFaces = 0;

                // Грани -> пластины плиты: 3 узла -> КЭ 42, 4 узла -> КЭ 44 (порядок узлов
                // КЭ 44 — "змейкой": p0 p1 p3 p2), больше 4 (висячие узлы) -> триангуляция.
                // Грань с центром пилона внутри разбивается веером треугольников вокруг
                // центра — центр становится узлом сетки, к нему цепляется стержень пилона.
                foreach (var face in faces)
                {
                    var poly = new System.Collections.Generic.List<Point2d>();
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
                var zLevels = new System.Collections.Generic.List<double> { 0.0 };
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

                    double tKey = System.Math.Round(thickness, 1);
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
        private System.Collections.Generic.List<System.Collections.Generic.List<int>> ExtractPlanarFaces(
            System.Collections.Generic.List<Point2d> nodes,
            System.Collections.Generic.List<int[]> edges)
        {
            int n = nodes.Count;
            var neighbors = new System.Collections.Generic.List<System.Collections.Generic.List<int>>();
            for (int i = 0; i < n; i++)
                neighbors.Add(new System.Collections.Generic.List<int>());

            var seenEdge = new System.Collections.Generic.HashSet<long>();
            foreach (var e in edges)
            {
                int a = e[0], b = e[1];
                long key = (long)System.Math.Min(a, b) * n + System.Math.Max(a, b);
                if (!seenEdge.Add(key)) continue;
                neighbors[a].Add(b);
                neighbors[b].Add(a);
            }

            for (int i = 0; i < n; i++)
            {
                int self = i;
                neighbors[i].Sort((p, q) =>
                {
                    double ap = System.Math.Atan2(nodes[p].Y - nodes[self].Y, nodes[p].X - nodes[self].X);
                    double aq = System.Math.Atan2(nodes[q].Y - nodes[self].Y, nodes[q].X - nodes[self].X);
                    return ap.CompareTo(aq);
                });
            }

            var visited = new System.Collections.Generic.HashSet<long>();
            var rawFaces = new System.Collections.Generic.List<System.Collections.Generic.List<int>>();
            var rawAreas = new System.Collections.Generic.List<double>();
            int maxSteps = edges.Count * 4 + 8;

            for (int start = 0; start < n; start++)
            {
                foreach (int firstNb in neighbors[start])
                {
                    if (visited.Contains((long)start * n + firstNb)) continue;

                    var face = new System.Collections.Generic.List<int> { start };
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
                    var poly = new System.Collections.Generic.List<Point2d>();
                    foreach (int idx in face) poly.Add(nodes[idx]);

                    double area = PolygonArea(poly);
                    if (System.Math.Abs(area) < 1e-3) continue;

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

            var result = new System.Collections.Generic.List<System.Collections.Generic.List<int>>();
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

        [CommandMethod("MESHQUADMESH")]
        public void HybridMeshCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            PromptEntityOptions peo = new PromptEntityOptions("\nВыберите замкнутый контур (полилинию): ");
            peo.SetRejectMessage("\nМожно выбрать только полилинию (LWPOLYLINE).");
            peo.AddAllowedClass(typeof(Polyline), false);

            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён или не удался.\n");
                return;
            }

            PromptDoubleOptions pdoSize = new PromptDoubleOptions("\nРазмер стороны элемента сетки: ");
            pdoSize.DefaultValue = 300.0;
            pdoSize.AllowNegative = false;
            pdoSize.AllowZero = false;
            pdoSize.Keywords.Add("300");
            pdoSize.Keywords.Add("400");
            pdoSize.Keywords.Add("500");
            pdoSize.AppendKeywordsToMessage = true;

            PromptDoubleResult pdrSize = ed.GetDouble(pdoSize);

            double cellSize;
            if (pdrSize.Status == PromptStatus.Keyword)
            {
                cellSize = double.Parse(pdrSize.StringResult);
            }
            else if (pdrSize.Status == PromptStatus.OK)
            {
                cellSize = pdrSize.Value;
            }
            else
            {
                ed.WriteMessage("\nВвод отменён.\n");
                return;
            }

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

                if (!ValidateContour(pline, ed, out var contourPts)) return;
                EnsureCcw(contourPts);

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var p in contourPts)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }

                int snappedWalls = SnapWallsToGrid(tr, db, minX, minY, cellSize);
                var wallSegments = GetWallSegments(tr, db);
                ed.WriteMessage($"\nНайдено сегментов стен: {wallSegments.Count}, подвинуто к узлам сетки (до {WallSnapTolerance:0} мм): {snappedWalls}\n");

                // Пилоны (слой COLUMNS): контур пилона врезается в сетку как стены,
                // внутри пилона строится своя сетка с шагом ColumnMeshStep в двух направлениях.
                int snappedColumns = SnapColumnsToGrid(tr, db, minX, minY, cellSize);
                var columnPolys = GetColumnPolygons(tr, db);
                var cutSegments = new System.Collections.Generic.List<Point2d[]>(wallSegments);
                foreach (var col in columnPolys)
                {
                    int cn = col.Count;
                    for (int i = 0; i < cn; i++)
                        cutSegments.Add(new Point2d[] { col[i], col[(i + 1) % cn] });
                }
                ed.WriteMessage($"\nНайдено пилонов: {columnPolys.Count}, подвинуто к сетке (до {WallSnapTolerance:0} мм): {snappedColumns}\n");

                var quadCells = new System.Collections.Generic.List<Point2d[]>();
                var boundaryCells = new System.Collections.Generic.List<Point2d[]>();
                var wallCells = new System.Collections.Generic.List<Point2d[]>();

                // Линии сетки допускается смещать к граням пилонов для чистоты разбиения
                // (увеличение ячейки ≤30% шага, но не более 100 мм).
                var colXs = new System.Collections.Generic.List<double>();
                var colYs = new System.Collections.Generic.List<double>();
                foreach (var col in columnPolys)
                {
                    foreach (var p in col)
                    {
                        colXs.Add(p.X);
                        colYs.Add(p.Y);
                    }
                }

                var xs = BuildGridCoords(minX, maxX, cellSize, colXs, out int shiftedX);
                var ys = BuildGridCoords(minY, maxY, cellSize, colYs, out int shiftedY);
                if (shiftedX + shiftedY > 0)
                    ed.WriteMessage($"\nЛиний сетки смещено к граням пилонов: {shiftedX + shiftedY}\n");

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
                            continue; // внутри пилона сетка плиты не нужна — там своя, 100 мм
                        }

                        if (CellTouchesWalls(cell, cutSegments))
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

                ed.WriteMessage($"\nПостроено квадратных элементов: {quadCells.Count}, ячеек у стен: {wallCells.Count}\n");

                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                var allSegments = new System.Collections.Generic.List<Point2d[]>();

                foreach (var cell in quadCells)
                {
                    AddQuadSegments(allSegments, cell);
                }

                // Кайма вдоль границы: каждую неполную ячейку сетки обрезаем по контуру
                // напрямую (Sutherland-Hodgman), без триангуляции всей полосы.
                var triVerts = new System.Collections.Generic.List<Point2d[]>();
                var directQuads = new System.Collections.Generic.List<Point2d[]>();
                int failedPolygons = 0;

                foreach (var cell in boundaryCells)
                {
                    var clipped = ClipPolygonToConvexCell(contourPts, cell);
                    clipped = CleanupPolygon(clipped);

                    if (clipped.Count < 3) continue;
                    if (System.Math.Abs(PolygonArea(clipped)) < 1e-3) continue;

                    if (clipped.Count == 4 && IsConvexQuad(clipped.ToArray()))
                    {
                        directQuads.Add(clipped.ToArray());
                    }
                    else
                    {
                        foreach (var tri in TriangulateSimplePolygon(clipped, ref failedPolygons))
                        {
                            if (System.Math.Abs(PolygonArea(new System.Collections.Generic.List<Point2d>(tri))) < 1e-3) continue;
                            triVerts.Add(tri);
                        }
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
                    if (System.Math.Abs(PolygonArea(clipped)) < 1e-3) continue;

                    foreach (var piece in SplitPolygonByWalls(clipped, cutSegments))
                    {
                        if (piece.Count < 3) continue;
                        if (System.Math.Abs(PolygonArea(piece)) < 1e-3) continue;
                        if (PieceInsideAnyColumn(piece, columnPolys)) continue;

                        if (piece.Count == 4 && IsConvexQuad(piece.ToArray()))
                        {
                            directQuads.Add(piece.ToArray());
                        }
                        else
                        {
                            foreach (var tri in TriangulateSimplePolygon(piece, ref failedPolygons))
                            {
                                if (System.Math.Abs(PolygonArea(new System.Collections.Generic.List<Point2d>(tri))) < 1e-3) continue;
                                triVerts.Add(tri);
                            }
                        }
                    }
                }

                foreach (var quad in directQuads)
                {
                    AddQuadSegments(allSegments, quad);
                }

                ed.WriteMessage($"\nТреугольников по краю (до объединения): {triVerts.Count}\n");

                // Справочник "сторона -> какие треугольники её используют"
                var edgeMap = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>();

                for (int i = 0; i < triVerts.Count; i++)
                {
                    var t = triVerts[i];
                    string[] keys = new string[]
                    {
                        EdgeKey(t[0], t[1]),
                        EdgeKey(t[1], t[2]),
                        EdgeKey(t[2], t[0])
                    };

                    foreach (var k in keys)
                    {
                        if (!edgeMap.ContainsKey(k))
                            edgeMap[k] = new System.Collections.Generic.List<int>();
                        edgeMap[k].Add(i);
                    }
                }

                // Жадное объединение пар треугольников в четырёхугольники
                bool[] used = new bool[triVerts.Count];
                var mergedQuads = new System.Collections.Generic.List<Point2d[]>();

                for (int i = 0; i < triVerts.Count; i++)
                {
                    if (used[i]) continue;

                    var t = triVerts[i];
                    string[] keys = new string[]
                    {
                        EdgeKey(t[0], t[1]),
                        EdgeKey(t[1], t[2]),
                        EdgeKey(t[2], t[0])
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
                        foreach (var w in cutSegments)
                            if (IsPointOnSegment(edgeMid, w[0], w[1], 1e-3)) { onWall = true; break; }
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

                ed.WriteMessage($"\nПрямых четырёхугольников по краю: {directQuads.Count}, получено четырёхугольников из объединения: {mergedQuads.Count}, осталось одиночных треугольников: {leftoverCount}\n");

                // Контроль качества: дальше элементы превращаются в отрезки и их форма
                // теряется, поэтому вырожденные элементы пересчитываются здесь.
                if (failedPolygons > 0)
                    ed.WriteMessage($"\nВНИМАНИЕ: не удалось триангулировать полигонов: {failedPolygons} — возможны дыры в сетке по краю или у стен.\n");

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
                if (poorTris + poorQuads > 0)
                    ed.WriteMessage($"\nВНИМАНИЕ: элементов с качеством α<{MinQualityAlpha:0.0#} (по методике ЛИРА-САПР): треугольников: {poorTris}, четырёхугольников: {poorQuads}, худший α={worstAlpha:0.00}\n");

                var uniqueSegments = DeduplicateSegments(allSegments);
                var innerSegments = RemoveSegmentsOnContour(uniqueSegments, contourPts, out int removedOnContour);
                innerSegments = ResolveOverlappingSegments(innerSegments, cutSegments, out int removedOnWalls, out int mergedOverlaps);

                // Рёбра короче MinElementSize (100 мм) недопустимы: подвижные узлы сетки
                // смещаются к неподвижной геометрии или сливаются друг с другом.
                innerSegments = WeldShortNodes(innerSegments, wallSegments, columnPolys, contourPts, out int weldedEdges);
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
                innerSegments = SplitSegmentsAtNodes(innerSegments, cellSize, out int splitEdges);

                // Открытые узлы недопустимы: точка, упершаяся в линию, замыкается
                // наклонной в соседний узел (угол по возможности близок к 30/45°).
                innerSegments = CloseOpenNodes(innerSegments, cutSegments, contourPts, columnPolys, cellSize, out int closedNodes);
                if (closedNodes > 0)
                    innerSegments = SplitSegmentsAtNodes(innerSegments, cellSize, out _);

                // Финальный шаг: сглаживание подвижных узлов для повышения качества α.
                innerSegments = SmoothMesh(innerSegments, cutSegments, contourPts, columnPolys, xs, ys, out int smoothedNodes);
                if (smoothedNodes > 0)
                    ed.WriteMessage($"\nСглажено узлов (Лаплас): {smoothedNodes}\n");

                foreach (var seg in innerSegments)
                    DrawSegment(btr, tr, seg[0], seg[1]);

                // Контур пилона разбивается на отрезки и переносится в слой линий
                // триангуляции (полилиния удаляется, точка центра остаётся в COLUMNS).
                int explodedColumns = ExplodeColumnContours(tr, db);
                if (explodedColumns > 0)
                    ed.WriteMessage($"\nКонтуров пилонов разбито на отрезки в {TriangulationLayerName}: {explodedColumns}\n");

                ed.WriteMessage($"\nОтрезков всего: {allSegments.Count}, после удаления совпадающих: {uniqueSegments.Count}, удалено по внешнему контуру: {removedOnContour}, срезано по стенам: {removedOnWalls}, устранено наложений: {mergedOverlaps}, схлопнуто коротких рёбер: {weldedEdges}, связей углов пилонов: {cornerLinks}, разбито рёбер узлами: {splitEdges}, замкнуто открытых узлов: {closedNodes}, итог: {innerSegments.Count}\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                // Транзакция не закоммичена — все изменения команды откатились.
                ed.WriteMessage($"\nОшибка MESHQUADMESH: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        [CommandMethod("MESHLAYERS")]
        public void CreateLayersCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите отрезки сетки и контур (Line + Polyline): ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE") });
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            PromptDoubleOptions pdo = new PromptDoubleOptions("\nТолщина плиты: ");
            pdo.AllowNegative = false;
            pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double slabThickness = pdr.Value;

            string slabLayerName = $"FOUNDATION_SLABS(H-{slabThickness:0.###})";
            string beamLayerName = TriangulationLayerName;

            var rnd = new System.Random();

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var usedColors = GetUsedLayerColors(db, tr);
                short slabColor = PickRandomColor(rnd, usedColors);
                short beamColor = PickRandomColor(rnd, usedColors);

                EnsureLayer(db, tr, slabLayerName, slabColor);
                EnsureLayer(db, tr, beamLayerName, beamColor);

                int slabCount = 0, beamCount = 0;
                int skippedWalls = 0;

                foreach (SelectedObject so in psr.Value)
                {
                    Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;

                    if (!string.IsNullOrEmpty(ent.Layer) && (ent.Layer.StartsWith("WALLS(H-") || IsColumnLayer(ent.Layer)))
                    {
                        skippedWalls++;
                        continue;
                    }

                    if (ent is Polyline)
                    {
                        ent.Layer = slabLayerName;
                        slabCount++;
                    }
                    else if (ent is Line)
                    {
                        ent.Layer = beamLayerName;
                        beamCount++;
                    }
                }

                // Контрольный проход: отрезки обязаны оказаться в слое триангуляции,
                // полилиния — в слое фундаментной плиты. Несовпадения исправляются.
                int fixedCount = 0;
                foreach (SelectedObject so in psr.Value)
                {
                    Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    if (!string.IsNullOrEmpty(ent.Layer) && (ent.Layer.StartsWith("WALLS(H-") || IsColumnLayer(ent.Layer))) continue;

                    string expected = null;
                    if (ent is Polyline) expected = slabLayerName;
                    else if (ent is Line) expected = beamLayerName;

                    if (expected != null && ent.Layer != expected)
                    {
                        ent.Layer = expected;
                        fixedCount++;
                    }
                }

                ed.WriteMessage($"\nПлита ({slabLayerName}): {slabCount}, триангуляция ({beamLayerName}): {beamCount}, пропущено стен: {skippedWalls}, исправлено проверкой: {fixedCount}\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHLAYERS: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        [CommandMethod("MESHWALLS")]
        public void CreateWallsLayerCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            PromptDoubleOptions pdo = new PromptDoubleOptions("\nТолщина стены: ");
            pdo.AllowNegative = false;
            pdo.AllowZero = false;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double wallThickness = pdr.Value;

            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите стены (Line + Polyline): ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE") });
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            string wallLayerName = $"WALLS(H-{wallThickness:0.###})";

            var rnd = new System.Random();

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var usedColors = GetUsedLayerColors(db, tr);
                short wallColor = PickRandomColor(rnd, usedColors);

                EnsureLayer(db, tr, wallLayerName, wallColor);

                int wallCount = 0;

                foreach (SelectedObject so in psr.Value)
                {
                    Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;

                    ent.Layer = wallLayerName;
                    wallCount++;
                }

                ed.WriteMessage($"\nСтены ({wallLayerName}): {wallCount}\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHWALLS: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        [CommandMethod("MESHCOLUMNS")]
        public void CreateColumnsLayerCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите пилоны (замкнутые полилинии): ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nВыбор отменён.\n");
                return;
            }

            var rnd = new System.Random();

            try
            {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var usedColors = GetUsedLayerColors(db, tr);

                // Слой на каждый типоразмер сечения (по габаритам bbox), цвета — из
                // палитры далеко разнесённых оттенков, без повторов с уже существующими.
                var sizeLayers = new System.Collections.Generic.Dictionary<string, string>();

                int columnCount = 0, skippedOpen = 0;

                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                foreach (SelectedObject so in psr.Value)
                {
                    Polyline pl = tr.GetObject(so.ObjectId, OpenMode.ForWrite) as Polyline;
                    if (pl == null) continue;

                    if (!pl.Closed)
                    {
                        skippedOpen++;
                        continue;
                    }

                    string layerName = ColumnLayerNameFor(pl);
                    if (!sizeLayers.ContainsKey(layerName))
                    {
                        EnsureLayer(db, tr, layerName, PickRandomColor(rnd, usedColors));
                        sizeLayers[layerName] = layerName;
                    }

                    pl.Layer = layerName;
                    columnCount++;

                    // Узел в центре сечения пилона — элемент POINT в том же слое
                    double cx = 0, cy = 0;
                    int n = pl.NumberOfVertices;
                    for (int i = 0; i < n; i++)
                    {
                        Point2d p = pl.GetPoint2dAt(i);
                        cx += p.X;
                        cy += p.Y;
                    }
                    DBPoint centerPt = new DBPoint(new Point3d(cx / n, cy / n, 0));
                    centerPt.Layer = layerName;
                    ms.AppendEntity(centerPt);
                    tr.AddNewlyCreatedDBObject(centerPt, true);
                }

                ed.WriteMessage($"\nПилоны: {columnCount} (+точки центров), типоразмеров/слоёв: {sizeLayers.Count}, пропущено незамкнутых: {skippedOpen}\n");
                foreach (var ln in sizeLayers.Keys)
                    ed.WriteMessage($"  {ln}\n");

                tr.Commit();
            }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHCOLUMNS: {ex.Message}\nИзменения команды отменены.\n");
            }
        }

        // Контур должен быть пригоден для построения сетки и импорта в ЛИРУ:
        // замкнут, без дуг (ЛИРА их не принимает), без самопересечений, с ненулевой
        // площадью. Иначе сетка молча строится кривой — лучше отказать сразу.
        private bool ValidateContour(Polyline pline, Editor ed, out System.Collections.Generic.List<Point2d> pts)
        {
            pts = null;

            if (!pline.Closed)
            {
                ed.WriteMessage("\nОшибка: полилиния не замкнута. Нужен замкнутый контур.\n");
                return false;
            }

            int nv = pline.NumberOfVertices;
            for (int i = 0; i < nv; i++)
            {
                if (pline.GetSegmentType(i) == SegmentType.Arc)
                {
                    ed.WriteMessage($"\nОшибка: контур содержит дугу (за вершиной {i}). Дуги не поддерживаются и не допускаются при импорте в ЛИРА-САПР — замените дуги хордами.\n");
                    return false;
                }
            }

            pts = CleanupPolygon(GetPolylineVertices(pline));
            if (pts.Count < 3 || System.Math.Abs(PolygonArea(pts)) < 1e-6)
            {
                ed.WriteMessage("\nОшибка: контур вырожден (меньше 3 несовпадающих вершин или нулевая площадь).\n");
                return false;
            }

            int m = pts.Count;
            for (int i = 0; i < m; i++)
            {
                for (int j = i + 1; j < m; j++)
                {
                    if (j == i + 1 || (i == 0 && j == m - 1)) continue; // смежные стороны

                    if (SegmentsIntersect(pts[i], pts[(i + 1) % m], pts[j], pts[(j + 1) % m]))
                    {
                        ed.WriteMessage($"\nОшибка: контур самопересекается (стороны {i}-{i + 1} и {j}-{(j + 1) % m}). Исправьте контур.\n");
                        return false;
                    }
                }
            }

            return true;
        }

        private const string ColumnLayerName = "COLUMNS";
        private const string TriangulationLayerName = "LINE_TRIANGULATION";

        // Пилоны лежат в слоях вида COLUMNS(SEC-RC_RECT B-600 H-300) — по одному слою
        // на типоразмер сечения; старый общий слой COLUMNS тоже распознаётся.
        private bool IsColumnLayer(string layer)
        {
            return !string.IsNullOrEmpty(layer) && layer.StartsWith(ColumnLayerName);
        }

        private string ColumnLayerNameFor(Polyline pl)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                Point2d p = pl.GetPoint2dAt(i);
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            return $"COLUMNS(SEC-RC_RECT B-{maxX - minX:0.###} H-{maxY - minY:0.###})";
        }
        private const double MinElementSize = 100.0;

        // Качество формы элемента по методике ЛИРА-САПР (мозаика "Качество пластин"):
        // α ∈ [0;1], равносторонний треугольник/квадрат = 1, α < 0.5 — плохой элемент.
        private const double MinQualityAlpha = 0.5;

        // Треугольник: α = 4√3·S / (a² + b² + c²).
        private double TriangleAlpha(Point2d a, Point2d b, Point2d c)
        {
            double ab = a.GetDistanceTo(b), bc = b.GetDistanceTo(c), ca = c.GetDistanceTo(a);
            double sumSq = ab * ab + bc * bc + ca * ca;
            if (sumSq < 1e-12) return 0.0;
            double area = System.Math.Abs(CrossProduct(a, b, c)) / 2.0;
            return 4.0 * System.Math.Sqrt(3.0) * area / sumSq;
        }

        // Четырёхугольник ABCD: α четырёх накладывающихся треугольников
        // {ABC, ACD, ABD, BCD}, итог — худшее из отношения произведений
        // противолежащих пар и отклонения среднего от √3/2 (α прямоугольного
        // равнобедренного треугольника, т.е. половины квадрата).
        private double QuadAlpha(Point2d[] q)
        {
            double a1 = TriangleAlpha(q[0], q[1], q[2]);
            double a2 = TriangleAlpha(q[0], q[2], q[3]);
            double a3 = TriangleAlpha(q[0], q[1], q[3]);
            double a4 = TriangleAlpha(q[1], q[2], q[3]);

            double t1 = a1 * a3, t2 = a2 * a4;
            if (t1 < 1e-12 || t2 < 1e-12) return 0.0;
            double alpha = t1 > t2 ? t2 / t1 : t1 / t2;

            const double rect = 0.8660254; // √3/2
            double avg = (rect - System.Math.Abs((a1 + a2 + a3 + a4) / 4.0 - rect)) / rect;

            return System.Math.Min(alpha, avg);
        }

        private bool QuadShapeOk(Point2d[] quad)
        {
            return QuadAlpha(quad) >= MinQualityAlpha;
        }

        // Разбивает замкнутые полилинии-контуры пилонов (слой COLUMNS) на отдельные
        // отрезки в слое линий триангуляции; исходная полилиния удаляется.
        private int ExplodeColumnContours(Transaction tr, Database db)
        {
            var plineIds = new System.Collections.Generic.List<ObjectId>();
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || !IsColumnLayer(ent.Layer)) continue;
                Polyline pl = ent as Polyline;
                if (pl == null || !pl.Closed) continue;
                plineIds.Add(id);
            }

            if (plineIds.Count == 0) return 0;

            var rnd = new System.Random();
            var usedColors = GetUsedLayerColors(db, tr);
            EnsureLayer(db, tr, TriangulationLayerName, PickRandomColor(rnd, usedColors));

            foreach (ObjectId id in plineIds)
            {
                Polyline pl = (Polyline)tr.GetObject(id, OpenMode.ForWrite);
                var verts = GetPolylineVertices(pl);
                int n = verts.Count;

                for (int i = 0; i < n; i++)
                {
                    Point2d a = verts[i];
                    Point2d b = verts[(i + 1) % n];
                    if (a.GetDistanceTo(b) < 1e-9) continue;

                    Line line = new Line(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0));
                    line.Layer = TriangulationLayerName;
                    btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);
                }

                pl.Erase();
            }

            return plineIds.Count;
        }

        // Пространственная сетка для поиска ближайших точек без перебора всех узлов на
        // каждый запрос: точки раскладываются по бакетам фиксированного размера, запрос
        // просматривает только бакеты, покрывающие нужный радиус. Без неё WeldShortNodes,
        // SplitSegmentsAtNodes, CloseOpenNodes и EnsureColumnCornerLinks были бы O(n²)
        // по числу узлов сетки, что на больших планах (тысячи сегментов) заметно тормозит.
        private class SpatialGrid
        {
            private readonly double cellSize;
            private readonly System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>> buckets =
                new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>>();

            public SpatialGrid(double cellSize)
            {
                this.cellSize = cellSize > 1e-9 ? cellSize : 1.0;
            }

            private static long Key(int cx, int cy)
            {
                return ((long)cx << 32) | (uint)cy;
            }

            private int CellOf(double v)
            {
                return (int)System.Math.Floor(v / cellSize);
            }

            public void Add(int index, Point2d p)
            {
                long key = Key(CellOf(p.X), CellOf(p.Y));
                System.Collections.Generic.List<int> list;
                if (!buckets.TryGetValue(key, out list))
                {
                    list = new System.Collections.Generic.List<int>();
                    buckets[key] = list;
                }
                list.Add(index);
            }

            public System.Collections.Generic.IEnumerable<int> QueryRadius(Point2d p, double radius)
            {
                int minCx = CellOf(p.X - radius), maxCx = CellOf(p.X + radius);
                int minCy = CellOf(p.Y - radius), maxCy = CellOf(p.Y + radius);

                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    for (int cy = minCy; cy <= maxCy; cy++)
                    {
                        System.Collections.Generic.List<int> list;
                        if (buckets.TryGetValue(Key(cx, cy), out list))
                        {
                            foreach (int idx in list) yield return idx;
                        }
                    }
                }
            }
        }

        // Сглаживание по Лапласу: подвижный узел смещается к среднему своих соседей —
        // поднимает качество α вытянутых элементов по краю. Неподвижны: узлы на контуре
        // плиты, стенах, гранях/центрах пилонов и узлы в пересечениях основной сетки
        // (их сдвиг портил бы правильные квадраты). Смещение отменяется, если узел
        // выходит из контура, попадает в пилон, создаёт ребро короче MinElementSize
        // или ребро, пересекающее стену/контур.
        private System.Collections.Generic.List<Point2d[]> SmoothMesh(
            System.Collections.Generic.List<Point2d[]> segments,
            System.Collections.Generic.List<Point2d[]> cutSegments,
            System.Collections.Generic.List<Point2d> contourPts,
            System.Collections.Generic.List<System.Collections.Generic.List<Point2d>> columnPolys,
            System.Collections.Generic.List<double> xs,
            System.Collections.Generic.List<double> ys,
            out int movedCount)
        {
            movedCount = 0;

            var nodes = new System.Collections.Generic.List<Point2d>();
            var nodeIndex = new System.Collections.Generic.Dictionary<string, int>();
            var neighbors = new System.Collections.Generic.List<System.Collections.Generic.HashSet<int>>();

            int GetNode(Point2d p)
            {
                string key = System.Math.Round(p.X, 3) + "_" + System.Math.Round(p.Y, 3);
                int idx;
                if (!nodeIndex.TryGetValue(key, out idx))
                {
                    idx = nodes.Count;
                    nodes.Add(p);
                    neighbors.Add(new System.Collections.Generic.HashSet<int>());
                    nodeIndex[key] = idx;
                }
                return idx;
            }

            var segNodes = new System.Collections.Generic.List<int[]>();
            foreach (var seg in segments)
            {
                int ia = GetNode(seg[0]);
                int ib = GetNode(seg[1]);
                if (ia == ib) continue;
                segNodes.Add(new int[] { ia, ib });
                neighbors[ia].Add(ib);
                neighbors[ib].Add(ia);
            }

            bool OnGridCoord(double v, System.Collections.Generic.List<double> coords)
            {
                foreach (var c in coords)
                    if (System.Math.Abs(v - c) < 1e-6) return true;
                return false;
            }

            var columnCenters = new System.Collections.Generic.List<Point2d>();
            foreach (var col in columnPolys)
            {
                double sx = 0, sy = 0;
                foreach (var p in col) { sx += p.X; sy += p.Y; }
                columnCenters.Add(new Point2d(sx / col.Count, sy / col.Count));
            }

            bool IsFixedNode(Point2d p)
            {
                if (OnGridCoord(p.X, xs) && OnGridCoord(p.Y, ys)) return true;

                foreach (var w in cutSegments)
                    if (IsPointOnSegment(p, w[0], w[1], 1e-3)) return true;

                foreach (var c in columnCenters)
                    if (p.GetDistanceTo(c) < 1e-3) return true;

                int cn = contourPts.Count;
                for (int i = 0; i < cn; i++)
                    if (IsPointOnSegment(p, contourPts[i], contourPts[(i + 1) % cn], 1e-3)) return true;

                return false;
            }

            var isFixed = new bool[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
                isFixed[i] = IsFixedNode(nodes[i]);

            bool SegmentBlocked(Point2d a, Point2d b)
            {
                foreach (var w in cutSegments)
                    if (SegmentsIntersect(a, b, w[0], w[1])) return true;

                int cn = contourPts.Count;
                for (int i = 0; i < cn; i++)
                    if (SegmentsIntersect(a, b, contourPts[i], contourPts[(i + 1) % cn])) return true;

                return false;
            }

            var wasMoved = new bool[nodes.Count];

            for (int iter = 0; iter < 2; iter++)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (isFixed[i] || neighbors[i].Count < 2) continue;

                    double ax = 0, ay = 0;
                    foreach (int nb in neighbors[i]) { ax += nodes[nb].X; ay += nodes[nb].Y; }
                    Point2d newP = new Point2d(ax / neighbors[i].Count, ay / neighbors[i].Count);

                    if (newP.GetDistanceTo(nodes[i]) < 1e-3) continue;
                    if (!IsPointInPolygon(newP, contourPts)) continue;

                    bool inColumn = false;
                    foreach (var col in columnPolys)
                        if (IsPointInPolygon(newP, col)) { inColumn = true; break; }
                    if (inColumn) continue;

                    bool bad = false;
                    foreach (int nb in neighbors[i])
                    {
                        if (newP.GetDistanceTo(nodes[nb]) < MinElementSize - 0.1) { bad = true; break; }
                        if (SegmentBlocked(newP, nodes[nb])) { bad = true; break; }
                    }
                    if (bad) continue;

                    nodes[i] = newP;
                    if (!wasMoved[i]) { wasMoved[i] = true; movedCount++; }
                }
            }

            var result = new System.Collections.Generic.List<Point2d[]>();
            foreach (var sn in segNodes)
            {
                Point2d a = nodes[sn[0]], b = nodes[sn[1]];
                if (a.GetDistanceTo(b) < 1e-3) continue;
                result.Add(new Point2d[] { a, b });
            }
            return result;
        }

        // В сетке не допускаются рёбра короче MinElementSize. Узлы, оказавшиеся ближе
        // 100 мм к неподвижной геометрии (стена, контур пилона, центр пилона, контур плиты),
        // притягиваются к ней; пары подвижных узлов ближе 100 мм сливаются в один.
        private System.Collections.Generic.List<Point2d[]> WeldShortNodes(
            System.Collections.Generic.List<Point2d[]> segments,
            System.Collections.Generic.List<Point2d[]> wallSegments,
            System.Collections.Generic.List<System.Collections.Generic.List<Point2d>> columnPolys,
            System.Collections.Generic.List<Point2d> contourPts,
            out int weldedCount)
        {
            weldedCount = 0;
            double weldDist = MinElementSize - 0.1;

            var columnCenters = new System.Collections.Generic.List<Point2d>();
            foreach (var col in columnPolys)
            {
                double sx = 0, sy = 0;
                foreach (var p in col) { sx += p.X; sy += p.Y; }
                columnCenters.Add(new Point2d(sx / col.Count, sy / col.Count));
            }

            var nodes = new System.Collections.Generic.List<Point2d>();
            var nodeIndex = new System.Collections.Generic.Dictionary<string, int>();

            int GetNode(Point2d p)
            {
                string key = System.Math.Round(p.X, 3) + "_" + System.Math.Round(p.Y, 3);
                int idx;
                if (!nodeIndex.TryGetValue(key, out idx))
                {
                    idx = nodes.Count;
                    nodes.Add(p);
                    nodeIndex[key] = idx;
                }
                return idx;
            }

            var segNodes = new System.Collections.Generic.List<int[]>();
            foreach (var seg in segments)
                segNodes.Add(new int[] { GetNode(seg[0]), GetNode(seg[1]) });

            bool IsFixedPoint(Point2d p)
            {
                foreach (var w in wallSegments)
                    if (IsPointOnSegment(p, w[0], w[1], 1e-3)) return true;

                foreach (var col in columnPolys)
                {
                    int n = col.Count;
                    for (int i = 0; i < n; i++)
                        if (IsPointOnSegment(p, col[i], col[(i + 1) % n], 1e-3)) return true;
                }

                foreach (var c in columnCenters)
                    if (p.GetDistanceTo(c) < 1e-3) return true;

                int cn = contourPts.Count;
                for (int i = 0; i < cn; i++)
                    if (IsPointOnSegment(p, contourPts[i], contourPts[(i + 1) % cn], 1e-3)) return true;

                return false;
            }

            var isFixed = new bool[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
                isFixed[i] = IsFixedPoint(nodes[i]);

            var target = new int[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
                target[i] = i;

            // Подвижный узел → ближайший фиксированный в радиусе сварки.
            // Кандидаты берутся из пространственной сетки (только соседние бакеты),
            // а не перебором всех узлов плана.
            var fixedGrid = new SpatialGrid(weldDist);
            for (int i = 0; i < nodes.Count; i++)
                if (isFixed[i]) fixedGrid.Add(i, nodes[i]);

            for (int i = 0; i < nodes.Count; i++)
            {
                if (isFixed[i]) continue;
                double bestDist = weldDist;
                int bestJ = -1;
                foreach (int j in fixedGrid.QueryRadius(nodes[i], weldDist))
                {
                    double d = nodes[i].GetDistanceTo(nodes[j]);
                    if (d < bestDist) { bestDist = d; bestJ = j; }
                }
                if (bestJ >= 0) target[i] = bestJ;
            }

            // Оставшиеся пары подвижных узлов ближе 100 мм — слить в узел с меньшим индексом
            var movableGrid = new SpatialGrid(weldDist);
            for (int i = 0; i < nodes.Count; i++)
                if (!isFixed[i]) movableGrid.Add(i, nodes[i]);

            for (int i = 0; i < nodes.Count; i++)
            {
                if (isFixed[i] || target[i] != i) continue;
                foreach (int j in movableGrid.QueryRadius(nodes[i], weldDist))
                {
                    if (j <= i || isFixed[j] || target[j] != j) continue;
                    if (nodes[i].GetDistanceTo(nodes[j]) < weldDist)
                        target[j] = i;
                }
            }

            var result = new System.Collections.Generic.List<Point2d[]>();
            for (int k = 0; k < segNodes.Count; k++)
            {
                Point2d a = nodes[target[segNodes[k][0]]];
                Point2d b = nodes[target[segNodes[k][1]]];
                if (a.GetDistanceTo(b) < 1e-3) { weldedCount++; continue; }
                result.Add(new Point2d[] { a, b });
            }
            return result;
        }

        // Координаты линий сетки: равномерный шаг, но линия, оказавшаяся ближе
        // min(30% шага, 100 мм) к грани пилона, смещается на неё — так проще,
        // чем городить наклонные линии.
        private System.Collections.Generic.List<double> BuildGridCoords(
            double min, double max, double step,
            System.Collections.Generic.List<double> targets,
            out int shiftedCount)
        {
            shiftedCount = 0;

            var coords = new System.Collections.Generic.List<double>();
            double v = min;
            while (v < max - 1e-9) { coords.Add(v); v += step; }
            coords.Add(v);

            double maxShift = System.Math.Min(0.3 * step, 100.0);

            foreach (var t in targets)
            {
                if (t < coords[0] - 1e-9 || t > coords[coords.Count - 1] + 1e-9) continue;

                int best = -1;
                double bestD = maxShift + 1e-9;
                for (int i = 0; i < coords.Count; i++)
                {
                    double d = System.Math.Abs(coords[i] - t);
                    if (d < bestD) { bestD = d; best = i; }
                }

                if (best >= 0 && System.Math.Abs(coords[best] - t) > 1e-9)
                {
                    coords[best] = t;
                    shiftedCount++;
                }
            }

            coords.Sort();
            var result = new System.Collections.Generic.List<double>();
            foreach (var c in coords)
            {
                if (result.Count > 0 && c - result[result.Count - 1] < 1e-6) continue;
                result.Add(c);
            }
            return result;
        }

        // Открытых узлов не допускается: узел, где линия упёрлась в другую линию и
        // остановилась (в веере направлений инцидентных отрезков есть пустой сектор
        // ≥180°), замыкается наклонной линией в соседний узел. Из кандидатов
        // предпочитается узел, дающий угол ближе к 30/45° к существующим линиям.
        private System.Collections.Generic.List<Point2d[]> CloseOpenNodes(
            System.Collections.Generic.List<Point2d[]> segments,
            System.Collections.Generic.List<Point2d[]> cutSegments,
            System.Collections.Generic.List<Point2d> contourPts,
            System.Collections.Generic.List<System.Collections.Generic.List<Point2d>> columnPolys,
            double cellSize,
            out int closedCount)
        {
            closedCount = 0;

            var nodes = new System.Collections.Generic.List<Point2d>();
            var nodeIndex = new System.Collections.Generic.Dictionary<string, int>();
            var dirs = new System.Collections.Generic.List<System.Collections.Generic.List<double>>();

            int GetNode(Point2d p)
            {
                string key = System.Math.Round(p.X, 3) + "_" + System.Math.Round(p.Y, 3);
                int idx;
                if (!nodeIndex.TryGetValue(key, out idx))
                {
                    idx = nodes.Count;
                    nodes.Add(p);
                    dirs.Add(new System.Collections.Generic.List<double>());
                    nodeIndex[key] = idx;
                }
                return idx;
            }

            foreach (var seg in segments)
            {
                int ia = GetNode(seg[0]);
                int ib = GetNode(seg[1]);
                dirs[ia].Add(System.Math.Atan2(seg[1].Y - seg[0].Y, seg[1].X - seg[0].X));
                dirs[ib].Add(System.Math.Atan2(seg[0].Y - seg[1].Y, seg[0].X - seg[1].X));
            }

            // Стены и грани пилонов — тоже линии сетки: покрывают направления вдоль себя
            foreach (var w in cutSegments)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (!IsPointOnSegment(nodes[i], w[0], w[1], 1e-3)) continue;
                    if (nodes[i].GetDistanceTo(w[0]) > 1e-3)
                        dirs[i].Add(System.Math.Atan2(w[0].Y - nodes[i].Y, w[0].X - nodes[i].X));
                    if (nodes[i].GetDistanceTo(w[1]) > 1e-3)
                        dirs[i].Add(System.Math.Atan2(w[1].Y - nodes[i].Y, w[1].X - nodes[i].X));
                }
            }

            var newSegs = new System.Collections.Generic.List<Point2d[]>();
            double twoPi = 2.0 * System.Math.PI;
            double candidateRadius = 1.6 * cellSize;

            // Кандидаты на замыкание ищутся только среди узлов из соседних бакетов сетки,
            // а не перебором всех узлов плана.
            var candidateGrid = new SpatialGrid(candidateRadius);
            for (int gi = 0; gi < nodes.Count; gi++)
                candidateGrid.Add(gi, nodes[gi]);

            for (int i = 0; i < nodes.Count; i++)
            {
                var dl = dirs[i];
                if (dl.Count < 2) continue;

                // узлы на контуре плиты не замыкаем — наружу нельзя
                bool onContour = false;
                int cn = contourPts.Count;
                for (int k = 0; k < cn; k++)
                    if (IsPointOnSegment(nodes[i], contourPts[k], contourPts[(k + 1) % cn], 1e-3)) { onContour = true; break; }
                if (onContour) continue;

                // узлы на контуре пилона не замыкаем — внутрь пилона сетка не идёт
                bool onColumn = false;
                foreach (var col in columnPolys)
                {
                    int nc = col.Count;
                    for (int k = 0; k < nc; k++)
                        if (IsPointOnSegment(nodes[i], col[k], col[(k + 1) % nc], 1e-3)) { onColumn = true; break; }
                    if (onColumn) break;
                }
                if (onColumn) continue;

                dl.Sort();
                double maxGap = 0, gapStart = 0;
                for (int k = 0; k < dl.Count; k++)
                {
                    double a0 = dl[k];
                    double a1 = (k + 1 < dl.Count) ? dl[k + 1] : dl[0] + twoPi;
                    double gap = a1 - a0;
                    if (gap > maxGap) { maxGap = gap; gapStart = a0; }
                }

                double gapDeg = maxGap * 180.0 / System.Math.PI;
                bool open = (dl.Count >= 3 && gapDeg >= 179.0) || (dl.Count == 2 && gapDeg >= 200.0);
                if (!open) continue;

                double margin = 15.0 * System.Math.PI / 180.0;
                double lo = gapStart + margin;
                double hi = gapStart + maxGap - margin;

                int best = -1;
                double bestScore = double.MaxValue;

                foreach (int j in candidateGrid.QueryRadius(nodes[i], candidateRadius))
                {
                    if (j == i) continue;
                    double d = nodes[i].GetDistanceTo(nodes[j]);
                    if (d < MinElementSize - 0.1 || d > candidateRadius) continue;

                    double a = System.Math.Atan2(nodes[j].Y - nodes[i].Y, nodes[j].X - nodes[i].X);
                    while (a < gapStart) a += twoPi;
                    if (a < lo || a > hi) continue;

                    bool crosses = false;
                    foreach (var w in cutSegments)
                        if (SegmentsIntersect(nodes[i], nodes[j], w[0], w[1])) { crosses = true; break; }
                    if (crosses) continue;

                    foreach (var s in segments)
                        if (SegmentsIntersect(nodes[i], nodes[j], s[0], s[1])) { crosses = true; break; }
                    if (crosses) continue;

                    // отклонение угла новой линии от 30/45° к границам пустого сектора
                    double d0 = (a - gapStart) * 180.0 / System.Math.PI;
                    double d1 = (gapStart + maxGap - a) * 180.0 / System.Math.PI;
                    double dev = System.Math.Min(
                        System.Math.Min(System.Math.Abs(d0 - 45.0), System.Math.Abs(d0 - 30.0)),
                        System.Math.Min(System.Math.Abs(d1 - 45.0), System.Math.Abs(d1 - 30.0)));

                    double score = d / cellSize + dev / 90.0;
                    if (score < bestScore) { bestScore = score; best = j; }
                }

                if (best >= 0)
                {
                    newSegs.Add(new Point2d[] { nodes[i], nodes[best] });
                    closedCount++;
                }
            }

            segments.AddRange(newSegs);
            return segments;
        }

        // Линия сетки не может обрываться посреди другого элемента: каждый узел,
        // лежащий внутри чужого отрезка, делит этот отрезок на два.
        private System.Collections.Generic.List<Point2d[]> SplitSegmentsAtNodes(
            System.Collections.Generic.List<Point2d[]> segments,
            double cellSize,
            out int splitCount)
        {
            splitCount = 0;

            var nodes = new System.Collections.Generic.List<Point2d>();
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var seg in segments)
            {
                foreach (var p in seg)
                {
                    string key = System.Math.Round(p.X, 3) + "_" + System.Math.Round(p.Y, 3);
                    if (seen.Add(key)) nodes.Add(p);
                }
            }

            // Узлы, потенциально лежащие на отрезке, ищутся через пространственную сетку
            // (только узлы в радиусе длины отрезка вокруг его начала), а не перебором всех узлов плана.
            var nodeGrid = new SpatialGrid(System.Math.Max(cellSize, 1.0));
            for (int ni = 0; ni < nodes.Count; ni++)
                nodeGrid.Add(ni, nodes[ni]);

            var result = new System.Collections.Generic.List<Point2d[]>();

            foreach (var seg in segments)
            {
                Point2d a = seg[0], b = seg[1];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double lenSq = dx * dx + dy * dy;
                if (lenSq < 1e-12) continue;
                double segLen = System.Math.Sqrt(lenSq);

                var cuts = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, Point2d>>();
                foreach (int nodeIdx in nodeGrid.QueryRadius(a, segLen))
                {
                    Point2d node = nodes[nodeIdx];
                    if (node.GetDistanceTo(a) < 1e-3 || node.GetDistanceTo(b) < 1e-3) continue;
                    if (!IsPointOnSegment(node, a, b, 1e-3)) continue;

                    double t = ((node.X - a.X) * dx + (node.Y - a.Y) * dy) / lenSq;
                    if (t > 1e-6 && t < 1.0 - 1e-6)
                        cuts.Add(new System.Collections.Generic.KeyValuePair<double, Point2d>(t, node));
                }

                if (cuts.Count == 0)
                {
                    result.Add(seg);
                    continue;
                }

                cuts.Sort((p, q) => p.Key.CompareTo(q.Key));
                Point2d prev = a;
                foreach (var cut in cuts)
                {
                    result.Add(new Point2d[] { prev, cut.Value });
                    prev = cut.Value;
                }
                result.Add(new Point2d[] { prev, b });
                splitCount += cuts.Count;
            }

            return result;
        }

        // Каждый угол пилона должен быть связан с сеткой минимум двумя отрезками
        // (полудиагональ к центру + связь наружу). Свободных углов не допускается.
        private System.Collections.Generic.List<Point2d[]> EnsureColumnCornerLinks(
            System.Collections.Generic.List<Point2d[]> segments,
            System.Collections.Generic.List<System.Collections.Generic.List<Point2d>> columnPolys,
            double cellSize,
            out int addedCount)
        {
            addedCount = 0;
            if (columnPolys.Count == 0) return segments;

            // Оба прохода (подсчёт инцидентных отрезков и поиск ближайшего узла) ищут
            // кандидатов через пространственную сетку концов отрезков, а не перебором
            // всех отрезков плана на каждый угол пилона.
            double queryRadius = cellSize + 1e-6;
            var endpoints = new System.Collections.Generic.List<Point2d>();
            var endpointGrid = new SpatialGrid(cellSize);
            foreach (var seg in segments)
            {
                endpointGrid.Add(endpoints.Count, seg[0]); endpoints.Add(seg[0]);
                endpointGrid.Add(endpoints.Count, seg[1]); endpoints.Add(seg[1]);
            }

            foreach (var col in columnPolys)
            {
                int n = col.Count;
                for (int ci = 0; ci < n; ci++)
                {
                    Point2d corner = col[ci];

                    int incident = 0;
                    foreach (int pi in endpointGrid.QueryRadius(corner, queryRadius))
                    {
                        if (endpoints[pi].GetDistanceTo(corner) < 1e-3)
                            incident++;
                    }
                    if (incident >= 2) continue;

                    // ближайший узел сетки снаружи пилона (не на его контуре)
                    Point2d best = corner;
                    double bestDist = double.MaxValue;
                    foreach (int pi in endpointGrid.QueryRadius(corner, queryRadius))
                    {
                        Point2d p = endpoints[pi];
                        double d = p.GetDistanceTo(corner);
                        if (d < 1e-3 || d > cellSize + 1e-6 || d >= bestDist) continue;
                        if (IsPointInPolygon(p, col)) continue;

                        bool onEdge = false;
                        for (int k = 0; k < n; k++)
                            if (IsPointOnSegment(p, col[k], col[(k + 1) % n], 1e-3)) { onEdge = true; break; }
                        if (onEdge) continue;

                        best = p;
                        bestDist = d;
                    }

                    if (bestDist < double.MaxValue)
                    {
                        segments.Add(new Point2d[] { corner, best });
                        addedCount++;
                    }
                }
            }

            return segments;
        }

        // Пилон допускается сдвигать целиком (жёсткий перенос, размеры сечения не меняются)
        // к линиям сетки не более чем на WallSnapTolerance мм — для чистоты сетки.
        // Привязка по левому нижнему углу bbox, покоординатно.
        private int SnapColumnsToGrid(Transaction tr, Database db, double minX, double minY, double cellSize)
        {
            int moved = 0;
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            var plineIds = new System.Collections.Generic.List<ObjectId>();
            var pointIds = new System.Collections.Generic.List<ObjectId>();

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || !IsColumnLayer(ent.Layer)) continue;

                Polyline pl = ent as Polyline;
                if (pl != null && pl.Closed) { plineIds.Add(id); continue; }
                if (ent is DBPoint) pointIds.Add(id);
            }

            foreach (ObjectId id in plineIds)
            {
                Polyline pl = (Polyline)tr.GetObject(id, OpenMode.ForRead);

                double cMinX = double.MaxValue, cMinY = double.MaxValue;
                int n = pl.NumberOfVertices;
                for (int i = 0; i < n; i++)
                {
                    Point2d p = pl.GetPoint2dAt(i);
                    if (p.X < cMinX) cMinX = p.X;
                    if (p.Y < cMinY) cMinY = p.Y;
                }

                double dx = SnapCoord(cMinX, minX, cellSize) - cMinX;
                double dy = SnapCoord(cMinY, minY, cellSize) - cMinY;

                if (System.Math.Abs(dx) < 1e-9 && System.Math.Abs(dy) < 1e-9) continue;

                // Точки центров (POINT в слое COLUMNS) внутри контура — до сдвига,
                // чтобы двигались вместе с пилоном
                var polyPts = GetPolylineVertices(pl);
                var disp = Matrix3d.Displacement(new Vector3d(dx, dy, 0));

                foreach (ObjectId pid in pointIds)
                {
                    DBPoint dp = (DBPoint)tr.GetObject(pid, OpenMode.ForRead);
                    Point2d pos = new Point2d(dp.Position.X, dp.Position.Y);
                    if (!IsPointInPolygon(pos, polyPts)) continue;

                    dp.UpgradeOpen();
                    dp.TransformBy(disp);
                }

                pl.UpgradeOpen();
                pl.TransformBy(disp);
                moved++;
            }

            return moved;
        }

        // Собирает замкнутые полилинии-сечения пилонов со слоя COLUMNS.
        private System.Collections.Generic.List<System.Collections.Generic.List<Point2d>> GetColumnPolygons(Transaction tr, Database db)
        {
            var result = new System.Collections.Generic.List<System.Collections.Generic.List<Point2d>>();
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || !IsColumnLayer(ent.Layer)) continue;

                Polyline pl = ent as Polyline;
                if (pl == null || !pl.Closed) continue;

                var verts = GetPolylineVertices(pl);
                if (verts.Count < 3) continue;
                EnsureCcw(verts);
                result.Add(verts);
            }

            return result;
        }

        private bool CellInsideAnyColumn(Point2d[] cell, System.Collections.Generic.List<System.Collections.Generic.List<Point2d>> columns)
        {
            foreach (var col in columns)
            {
                bool allInside = true;
                foreach (var corner in cell)
                {
                    if (!IsPointInPolygon(corner, col)) { allInside = false; break; }
                }
                if (allInside) return true;
            }
            return false;
        }

        private bool PieceInsideAnyColumn(System.Collections.Generic.List<Point2d> piece, System.Collections.Generic.List<System.Collections.Generic.List<Point2d>> columns)
        {
            if (columns.Count == 0) return false;

            double cx = 0, cy = 0;
            foreach (var p in piece) { cx += p.X; cy += p.Y; }
            Point2d centroid = new Point2d(cx / piece.Count, cy / piece.Count);

            foreach (var col in columns)
            {
                if (IsPointInPolygon(centroid, col)) return true;
            }
            return false;
        }

        private static readonly short[] LayerColorPalette = new short[] { 1, 2, 3, 4, 5, 6, 30, 50, 90, 140, 200, 220 };

        // Цвет слоя ни при каких обстоятельствах не должен совпадать с цветом
        // уже существующего слоя (used заполняется из таблицы слоёв чертежа).
        private short PickRandomColor(System.Random rnd, System.Collections.Generic.HashSet<short> used)
        {
            var available = new System.Collections.Generic.List<short>();
            foreach (var c in LayerColorPalette)
                if (!used.Contains(c)) available.Add(c);

            if (available.Count > 0)
            {
                short color = available[rnd.Next(available.Count)];
                used.Add(color);
                return color;
            }

            // Палитра исчерпана — берём первый свободный ACI-цвет (7 = белый, пропускаем).
            for (short c = 1; c <= 255; c++)
            {
                if (c == 7 || used.Contains(c)) continue;
                used.Add(c);
                return c;
            }
            return 1;
        }

        private System.Collections.Generic.HashSet<short> GetUsedLayerColors(Database db, Transaction tr)
        {
            var used = new System.Collections.Generic.HashSet<short>();
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                LayerTableRecord ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                used.Add(ltr.Color.ColorIndex);
            }
            return used;
        }

        private void EnsureLayer(Database db, Transaction tr, string layerName, short colorIndex)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layerName)) return;

            lt.UpgradeOpen();
            LayerTableRecord ltr = new LayerTableRecord();
            ltr.Name = layerName;
            ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex);
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }

        private string EdgeKey(Point2d a, Point2d b)
        {
            string ka = System.Math.Round(a.X, 3) + "_" + System.Math.Round(a.Y, 3);
            string kb = System.Math.Round(b.X, 3) + "_" + System.Math.Round(b.Y, 3);
            return string.CompareOrdinal(ka, kb) < 0 ? ka + "|" + kb : kb + "|" + ka;
        }

        private Point2d FindOppositeVertex(Point2d[] tri, Point2d a, Point2d b)
        {
            double tol = 1e-3;
            foreach (var v in tri)
            {
                bool closeToA = System.Math.Abs(v.X - a.X) < tol && System.Math.Abs(v.Y - a.Y) < tol;
                bool closeToB = System.Math.Abs(v.X - b.X) < tol && System.Math.Abs(v.Y - b.Y) < tol;

                if (!closeToA && !closeToB)
                    return v;
            }
            return tri[0];
        }

        private bool IsConvexQuad(Point2d[] quad)
        {
            int sign = 0;
            for (int i = 0; i < 4; i++)
            {
                Point2d p0 = quad[i];
                Point2d p1 = quad[(i + 1) % 4];
                Point2d p2 = quad[(i + 2) % 4];

                double cross = CrossProduct(p0, p1, p2);
                int s = cross > 0 ? 1 : (cross < 0 ? -1 : 0);

                if (s == 0) continue;

                if (sign == 0) sign = s;
                else if (s != sign) return false;
            }
            return true;
        }

        private void DrawSegment(BlockTableRecord btr, Transaction tr, Point2d a, Point2d b)
        {
            Line line = new Line(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0));
            btr.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
        }

        private void AddQuadSegments(System.Collections.Generic.List<Point2d[]> segments, Point2d[] quad)
        {
            segments.Add(new Point2d[] { quad[0], quad[1] });
            segments.Add(new Point2d[] { quad[1], quad[2] });
            segments.Add(new Point2d[] { quad[2], quad[3] });
            segments.Add(new Point2d[] { quad[3], quad[0] });
        }

        private void AddTriSegments(System.Collections.Generic.List<Point2d[]> segments, Point2d[] tri)
        {
            segments.Add(new Point2d[] { tri[0], tri[1] });
            segments.Add(new Point2d[] { tri[1], tri[2] });
            segments.Add(new Point2d[] { tri[2], tri[0] });
        }

        // Совпадающие отрезки (общее ребро двух соседних ячеек/треугольников) не должны
        // попадать в DXF дважды: ЛИРА-САПР требует, чтобы отрезки на плане не накладывались.
        private System.Collections.Generic.List<Point2d[]> DeduplicateSegments(System.Collections.Generic.List<Point2d[]> segments)
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            var result = new System.Collections.Generic.List<Point2d[]>();
            foreach (var seg in segments)
            {
                string key = EdgeKey(seg[0], seg[1]);
                if (seen.Add(key))
                    result.Add(seg);
            }
            return result;
        }

        private bool IsPointOnSegment(Point2d p, Point2d a, Point2d b, double eps)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-12) return false;

            double len = System.Math.Sqrt(lenSq);
            double dist = System.Math.Abs(CrossProduct(a, b, p)) / len;
            if (dist > eps) return false;

            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
            double tolT = eps / len;
            return t >= -tolT && t <= 1 + tolT;
        }

        private bool SegmentLiesOnContour(Point2d a, Point2d b, System.Collections.Generic.List<Point2d> contour, double eps = 1e-3)
        {
            int n = contour.Count;
            for (int i = 0; i < n; i++)
            {
                Point2d c1 = contour[i];
                Point2d c2 = contour[(i + 1) % n];

                if (IsPointOnSegment(a, c1, c2, eps) && IsPointOnSegment(b, c1, c2, eps))
                    return true;
            }
            return false;
        }

        // Внешний контур плиты уже представлен исходной полилинией — отрезки сетки,
        // легшие на него (целиком или как часть более длинного ребра контура), в DXF не нужны.
        private System.Collections.Generic.List<Point2d[]> RemoveSegmentsOnContour(
            System.Collections.Generic.List<Point2d[]> segments,
            System.Collections.Generic.List<Point2d> contour,
            out int removedCount)
        {
            var result = new System.Collections.Generic.List<Point2d[]>();
            removedCount = 0;
            foreach (var seg in segments)
            {
                if (SegmentLiesOnContour(seg[0], seg[1], contour))
                    removedCount++;
                else
                    result.Add(seg);
            }
            return result;
        }

        private const double WallSnapTolerance = 100.0;

        private double SnapCoord(double v, double origin, double cellSize)
        {
            double snapped = origin + System.Math.Round((v - origin) / cellSize) * cellSize;
            return System.Math.Abs(snapped - v) <= WallSnapTolerance ? snapped : v;
        }

        // Стены допускается сдвигать к линиям сетки не более чем на WallSnapTolerance мм —
        // это убирает узкие полосы между стеной и рядом идущей линией сетки.
        // Двигаются сами объекты на слоях WALLS(H-...), чтобы чертёж совпадал с сеткой.
        private int SnapWallsToGrid(Transaction tr, Database db, double minX, double minY, double cellSize)
        {
            int moved = 0;
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                if (string.IsNullOrEmpty(ent.Layer) || !ent.Layer.StartsWith("WALLS(H-")) continue;

                Line line = ent as Line;
                if (line != null)
                {
                    Point3d s = line.StartPoint;
                    Point3d e = line.EndPoint;
                    Point3d ns = new Point3d(SnapCoord(s.X, minX, cellSize), SnapCoord(s.Y, minY, cellSize), s.Z);
                    Point3d ne = new Point3d(SnapCoord(e.X, minX, cellSize), SnapCoord(e.Y, minY, cellSize), e.Z);

                    if (ns.DistanceTo(s) > 1e-9 || ne.DistanceTo(e) > 1e-9)
                    {
                        line.UpgradeOpen();
                        line.StartPoint = ns;
                        line.EndPoint = ne;
                        moved++;
                    }
                    continue;
                }

                Polyline wallPline = ent as Polyline;
                if (wallPline != null)
                {
                    bool changed = false;
                    int n = wallPline.NumberOfVertices;
                    for (int i = 0; i < n; i++)
                    {
                        Point2d p = wallPline.GetPoint2dAt(i);
                        Point2d np = new Point2d(SnapCoord(p.X, minX, cellSize), SnapCoord(p.Y, minY, cellSize));
                        if (np.GetDistanceTo(p) > 1e-9)
                        {
                            if (!changed) { wallPline.UpgradeOpen(); changed = true; }
                            wallPline.SetPointAt(i, np);
                        }
                    }
                    if (changed) moved++;
                }
            }

            return moved;
        }

        private class CollinearGroup
        {
            public System.Collections.Generic.List<double[]> Mesh = new System.Collections.Generic.List<double[]>();
            public System.Collections.Generic.List<double[]> Blocked = new System.Collections.Generic.List<double[]>();
            public System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, Point2d>> Breaks =
                new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, Point2d>>();
        }

        private void AddSegmentToLineGroups(System.Collections.Generic.Dictionary<string, CollinearGroup> groups, Point2d a, Point2d b, bool blocked)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = System.Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return;

            double nx = dx / len, ny = dy / len;
            if (nx < -1e-9 || (System.Math.Abs(nx) <= 1e-9 && ny < 0)) { nx = -nx; ny = -ny; }
            double c = a.X * ny - a.Y * nx;

            string key = nx.ToString("F6") + "|" + ny.ToString("F6") + "|" + c.ToString("F1");

            CollinearGroup g;
            if (!groups.TryGetValue(key, out g))
            {
                g = new CollinearGroup();
                groups[key] = g;
            }

            double t0 = a.X * nx + a.Y * ny;
            double t1 = b.X * nx + b.Y * ny;
            Point2d pa = a, pb = b;
            if (t0 > t1)
            {
                double tt = t0; t0 = t1; t1 = tt;
                Point2d tp = pa; pa = pb; pb = tp;
            }

            g.Breaks.Add(new System.Collections.Generic.KeyValuePair<double, Point2d>(t0, pa));
            g.Breaks.Add(new System.Collections.Generic.KeyValuePair<double, Point2d>(t1, pb));
            (blocked ? g.Blocked : g.Mesh).Add(new double[] { t0, t1 });
        }

        // Финальная зачистка: никакие два отрезка (и отрезок со стеной) не должны
        // накладываться даже частично. Коллинеарные отрезки на одной прямой разбиваются
        // концами друг друга на элементарные интервалы; каждый интервал выводится один раз,
        // интервалы, накрытые стеной, выбрасываются (стена уже нарисована пользователем).
        private System.Collections.Generic.List<Point2d[]> ResolveOverlappingSegments(
            System.Collections.Generic.List<Point2d[]> meshSegments,
            System.Collections.Generic.List<Point2d[]> wallSegments,
            out int removedOnWalls,
            out int mergedOverlaps)
        {
            removedOnWalls = 0;
            mergedOverlaps = 0;

            var groups = new System.Collections.Generic.Dictionary<string, CollinearGroup>();
            foreach (var seg in meshSegments)
                AddSegmentToLineGroups(groups, seg[0], seg[1], false);
            foreach (var seg in wallSegments)
                AddSegmentToLineGroups(groups, seg[0], seg[1], true);

            var result = new System.Collections.Generic.List<Point2d[]>();

            foreach (var g in groups.Values)
            {
                if (g.Mesh.Count == 0) continue;

                g.Breaks.Sort((p, q) => p.Key.CompareTo(q.Key));

                var ts = new System.Collections.Generic.List<double>();
                var pts = new System.Collections.Generic.List<Point2d>();
                foreach (var br in g.Breaks)
                {
                    if (ts.Count > 0 && br.Key - ts[ts.Count - 1] < 1e-3) continue;
                    ts.Add(br.Key);
                    pts.Add(br.Value);
                }

                for (int i = 0; i + 1 < ts.Count; i++)
                {
                    double mid = (ts[i] + ts[i + 1]) / 2.0;

                    int cover = 0;
                    foreach (var iv in g.Mesh)
                        if (mid >= iv[0] - 1e-6 && mid <= iv[1] + 1e-6) cover++;
                    if (cover == 0) continue;

                    bool blocked = false;
                    foreach (var iv in g.Blocked)
                        if (mid >= iv[0] - 1e-6 && mid <= iv[1] + 1e-6) { blocked = true; break; }

                    if (blocked) { removedOnWalls++; continue; }
                    if (cover > 1) mergedOverlaps += cover - 1;

                    result.Add(new Point2d[] { pts[i], pts[i + 1] });
                }
            }

            return result;
        }

        // Собирает отрезки стен со всех слоёв WALLS(H-...), созданных командой MESHWALLS.
        private System.Collections.Generic.List<Point2d[]> GetWallSegments(Transaction tr, Database db)
        {
            var result = new System.Collections.Generic.List<Point2d[]>();
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                if (string.IsNullOrEmpty(ent.Layer) || !ent.Layer.StartsWith("WALLS(H-")) continue;

                Line line = ent as Line;
                if (line != null)
                {
                    result.Add(new Point2d[]
                    {
                        new Point2d(line.StartPoint.X, line.StartPoint.Y),
                        new Point2d(line.EndPoint.X, line.EndPoint.Y)
                    });
                    continue;
                }

                Polyline wallPline = ent as Polyline;
                if (wallPline != null)
                {
                    var verts = GetPolylineVertices(wallPline);
                    int n = verts.Count;
                    int segCount = wallPline.Closed ? n : n - 1;
                    for (int i = 0; i < segCount; i++)
                    {
                        result.Add(new Point2d[] { verts[i], verts[(i + 1) % n] });
                    }
                }
            }

            return result;
        }

        // Ячейка "задета" стеной, если стена проходит через неё хотя бы частично.
        private bool CellTouchesWalls(Point2d[] cell, System.Collections.Generic.List<Point2d[]> wallSegments)
        {
            if (wallSegments.Count == 0) return false;

            var cellPoly = new System.Collections.Generic.List<Point2d>(cell);
            foreach (var w in wallSegments)
            {
                if (IsPointInPolygon(w[0], cellPoly) || IsPointInPolygon(w[1], cellPoly)) return true;

                for (int k = 0; k < 4; k++)
                {
                    if (SegmentsIntersect(w[0], w[1], cell[k], cell[(k + 1) % 4])) return true;
                }
            }
            return false;
        }

        private bool SegmentTouchesPolygon(Point2d a, Point2d b, System.Collections.Generic.List<Point2d> poly)
        {
            if (IsPointInPolygon(a, poly) || IsPointInPolygon(b, poly)) return true;

            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                if (SegmentsIntersect(a, b, poly[i], poly[(i + 1) % n])) return true;
            }

            Point2d mid = new Point2d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
            return IsPointInPolygon(mid, poly);
        }

        // Последовательно разрезает полигон ячейки по линии каждой задевшей его стены:
        // обе полуплоскости через Sutherland-Hodgman. Точки разреза общие для соседних
        // частей — стена входит в сетку как рёбра с совпадающими узлами.
        private System.Collections.Generic.List<System.Collections.Generic.List<Point2d>> SplitPolygonByWalls(
            System.Collections.Generic.List<Point2d> poly,
            System.Collections.Generic.List<Point2d[]> wallSegments)
        {
            var pieces = new System.Collections.Generic.List<System.Collections.Generic.List<Point2d>> { poly };

            foreach (var w in wallSegments)
            {
                var next = new System.Collections.Generic.List<System.Collections.Generic.List<Point2d>>();

                foreach (var piece in pieces)
                {
                    if (!SegmentTouchesPolygon(w[0], w[1], piece))
                    {
                        next.Add(piece);
                        continue;
                    }

                    var left = CleanupPolygon(ClipPolygonAgainstEdge(piece, w[0], w[1]));
                    var right = CleanupPolygon(ClipPolygonAgainstEdge(piece, w[1], w[0]));

                    bool added = false;
                    if (left.Count >= 3 && System.Math.Abs(PolygonArea(left)) > 1e-3) { next.Add(left); added = true; }
                    if (right.Count >= 3 && System.Math.Abs(PolygonArea(right)) > 1e-3) { next.Add(right); added = true; }
                    if (!added) next.Add(piece);
                }

                pieces = next;
            }

            return pieces;
        }

        private System.Collections.Generic.List<Point2d> GetPolylineVertices(Polyline pline)
        {
            var result = new System.Collections.Generic.List<Point2d>();
            int n = pline.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                result.Add(pline.GetPoint2dAt(i));
            }
            return result;
        }

        private bool IsPointInPolygon(Point2d point, System.Collections.Generic.List<Point2d> polygon)
        {
            bool inside = false;
            int n = polygon.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Point2d pi = polygon[i];
                Point2d pj = polygon[j];

                bool edgeCrosses = ((pi.Y > point.Y) != (pj.Y > point.Y)) &&
                    (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X);

                if (edgeCrosses)
                    inside = !inside;
            }
            return inside;
        }

        private double CrossProduct(Point2d a, Point2d b, Point2d c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        private bool SegmentsIntersect(Point2d p1, Point2d p2, Point2d p3, Point2d p4)
        {
            double d1 = CrossProduct(p3, p4, p1);
            double d2 = CrossProduct(p3, p4, p2);
            double d3 = CrossProduct(p1, p2, p3);
            double d4 = CrossProduct(p1, p2, p4);

            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                   ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private bool IsCellFullyInside(Point2d[] cell, System.Collections.Generic.List<Point2d> contour)
        {
            foreach (var corner in cell)
            {
                if (!IsPointInPolygon(corner, contour))
                    return false;
            }

            int n = contour.Count;
            for (int i = 0; i < n; i++)
            {
                Point2d c1 = contour[i];
                Point2d c2 = contour[(i + 1) % n];

                for (int k = 0; k < 4; k++)
                {
                    Point2d s1 = cell[k];
                    Point2d s2 = cell[(k + 1) % 4];

                    if (SegmentsIntersect(c1, c2, s1, s2))
                        return false;
                }
            }

            return true;
        }

        private double PolygonArea(System.Collections.Generic.List<Point2d> poly)
        {
            double area = 0.0;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                Point2d a = poly[i];
                Point2d b = poly[(i + 1) % n];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area / 2.0;
        }

        private void EnsureCcw(System.Collections.Generic.List<Point2d> poly)
        {
            if (PolygonArea(poly) < 0)
                poly.Reverse();
        }

        private Point2d LineIntersection(Point2d p1, Point2d p2, Point2d clipA, Point2d clipB)
        {
            double d1 = CrossProduct(clipA, clipB, p1);
            double d2 = CrossProduct(clipA, clipB, p2);
            double denom = d1 - d2;
            if (System.Math.Abs(denom) < 1e-9)
                denom = (denom >= 0 ? 1e-9 : -1e-9);
            double t = d1 / denom;
            return new Point2d(p1.X + t * (p2.X - p1.X), p1.Y + t * (p2.Y - p1.Y));
        }

        private System.Collections.Generic.List<Point2d> ClipPolygonAgainstEdge(System.Collections.Generic.List<Point2d> subject, Point2d clipA, Point2d clipB)
        {
            var output = new System.Collections.Generic.List<Point2d>();
            int n = subject.Count;
            if (n == 0) return output;

            for (int i = 0; i < n; i++)
            {
                Point2d current = subject[i];
                Point2d prev = subject[(i - 1 + n) % n];

                bool currentInside = CrossProduct(clipA, clipB, current) >= 0;
                bool prevInside = CrossProduct(clipA, clipB, prev) >= 0;

                if (currentInside)
                {
                    if (!prevInside)
                        output.Add(LineIntersection(prev, current, clipA, clipB));
                    output.Add(current);
                }
                else if (prevInside)
                {
                    output.Add(LineIntersection(prev, current, clipA, clipB));
                }
            }

            return output;
        }

        private System.Collections.Generic.List<Point2d> ClipPolygonToConvexCell(System.Collections.Generic.List<Point2d> subject, Point2d[] cell)
        {
            var result = new System.Collections.Generic.List<Point2d>(subject);
            int n = cell.Length;
            for (int i = 0; i < n && result.Count > 0; i++)
            {
                result = ClipPolygonAgainstEdge(result, cell[i], cell[(i + 1) % n]);
            }
            return result;
        }

        private System.Collections.Generic.List<Point2d> CleanupPolygon(System.Collections.Generic.List<Point2d> poly, double eps = 1e-3)
        {
            var result = new System.Collections.Generic.List<Point2d>();
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                Point2d p = poly[i];
                if (result.Count > 0)
                {
                    Point2d last = result[result.Count - 1];
                    if (System.Math.Abs(p.X - last.X) < eps && System.Math.Abs(p.Y - last.Y) < eps)
                        continue;
                }
                result.Add(p);
            }

            if (result.Count > 1)
            {
                Point2d first = result[0];
                Point2d last = result[result.Count - 1];
                if (System.Math.Abs(first.X - last.X) < eps && System.Math.Abs(first.Y - last.Y) < eps)
                    result.RemoveAt(result.Count - 1);
            }

            return result;
        }

        private bool IsPointInTriangle(Point2d p, Point2d a, Point2d b, Point2d c)
        {
            double d1 = CrossProduct(a, b, p);
            double d2 = CrossProduct(b, c, p);
            double d3 = CrossProduct(c, a, p);

            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;

            return !(hasNeg && hasPos);
        }

        private System.Collections.Generic.List<Point2d[]> TriangulateSimplePolygon(
            System.Collections.Generic.List<Point2d> poly,
            ref int failedPolygons)
        {
            var result = new System.Collections.Generic.List<Point2d[]>();
            var verts = new System.Collections.Generic.List<Point2d>(poly);

            if (verts.Count < 3) return result;
            if (PolygonArea(verts) < 0) verts.Reverse();

            int guard = 0;
            while (verts.Count > 3 && guard < 1000)
            {
                guard++;
                bool earFound = false;
                int n = verts.Count;

                for (int i = 0; i < n; i++)
                {
                    Point2d prev = verts[(i - 1 + n) % n];
                    Point2d cur = verts[i];
                    Point2d next = verts[(i + 1) % n];

                    if (CrossProduct(prev, cur, next) <= 0) continue;

                    bool anyInside = false;
                    for (int k = 0; k < n; k++)
                    {
                        if (k == (i - 1 + n) % n || k == i || k == (i + 1) % n) continue;
                        if (IsPointInTriangle(verts[k], prev, cur, next)) { anyInside = true; break; }
                    }
                    if (anyInside) continue;

                    result.Add(new Point2d[] { prev, cur, next });
                    verts.RemoveAt(i);
                    earFound = true;
                    break;
                }

                if (!earFound) break;
            }

            if (verts.Count == 3)
            {
                result.Add(new Point2d[] { verts[0], verts[1], verts[2] });
            }
            else if (verts.Count > 3)
            {
                // Ушная триангуляция застряла (полигон почти вырожден или с дефектом) —
                // запасной вариант: веер от центроида, если все его треугольники корректны.
                // Иначе остаток полигона теряется, что учитывается в failedPolygons.
                double cx = 0, cy = 0;
                foreach (var p in verts) { cx += p.X; cy += p.Y; }
                Point2d c = new Point2d(cx / verts.Count, cy / verts.Count);

                var fan = new System.Collections.Generic.List<Point2d[]>();
                bool fanOk = true;
                int m = verts.Count;
                for (int i = 0; i < m; i++)
                {
                    Point2d a = verts[i];
                    Point2d b = verts[(i + 1) % m];
                    if (CrossProduct(a, b, c) <= 2e-3) { fanOk = false; break; }
                    fan.Add(new Point2d[] { a, b, c });
                }

                if (fanOk)
                    result.AddRange(fan);
                else
                    failedPolygons++;
            }

            return result;
        }
    }
}