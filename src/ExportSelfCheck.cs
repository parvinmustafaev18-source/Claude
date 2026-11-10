using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    // ПРОВЕРКА РЕЗУЛЬТАТА ЭКСПОРТА для самотеста.
    //
    // Эталонного файла задачи взять неоткуда, поэтому проверяются правила, которым
    // схема обязана отвечать при любом входе: элементы ссылаются на существующие узлы
    // и на объявленные жёсткости, у каждого типа КЭ своё число узлов, одинаковых
    // элементов нет, а пластины плиты покрывают ровно площадь контура за вычетом
    // отверстий. Последнее — главное: недобор площади и есть дыра в схеме ЛИРЫ,
    // из-за которой раньше приходилось искать проблему уже в самой ЛИРЕ.
    public partial class Commands
    {
        // Что бы увидел экспорт, если бы этот план начертили и построили по нему сетку:
        // линии сетки лежат в LINE_TRIANGULATION, стены остались на своём слое, контур
        // и кромки отверстий тоже попадают в планарный граф.
        private ExportInput MeshToExportInput(MeshInput mesh, MeshResult res)
        {
            var input = new ExportInput
            {
                Contour = mesh.Contour,
                HolePolys = mesh.HolePolys,
                PylonRects = mesh.PylonRects,
                TaskName = "SELFTEST_LIRA"
            };

            int cn = mesh.Contour.Count;
            for (int i = 0; i < cn; i++)
                input.Segments.Add(new Point2d[] { mesh.Contour[i], mesh.Contour[(i + 1) % cn] });

            foreach (var h in mesh.HolePolys)
            {
                int hc = h.Count;
                for (int i = 0; i < hc; i++)
                    input.Segments.Add(new Point2d[] { h[i], h[(i + 1) % hc] });
            }

            foreach (var seg in res.Segments)
                input.Segments.Add(new Point2d[] { seg[0], seg[1] });

            // Стены: в выдуманном плане толщина одна на всех — 200 мм, как у слоя
            // WALLS(H-200), который ставит LIRWALLS по умолчанию.
            foreach (var w in mesh.WallSegments)
            {
                input.Segments.Add(new Point2d[] { w[0], w[1] });
                input.WallOrig.Add(w);
                input.WallThickness.Add(200.0);
                input.WallIsPylon.Add(false);
                input.WallSizeKey.Add(null);
            }

            // Пилоны-стержни: центр сечения и габариты — то же, что даёт точка в COLUMNS.
            foreach (var col in mesh.ColumnPolys)
            {
                var bb = PolygonBBox(col);
                input.ColumnCenters.Add(PolygonCentroid(col));
                input.ColumnDims.Add(new double[] { bb[2] - bb[0], bb[3] - bb[1] });
            }

            return input;
        }

        private List<string> CheckExportInvariants(ExportInput input, ExportResult task)
        {
            var bad = new List<string>();

            if (!task.Ok)
            {
                bad.Add("экспорт не дал схемы" + (string.IsNullOrEmpty(task.Error) ? "" : ": " + task.Error.Trim()));
                return bad;
            }

            if (task.Elements.Count == 0) { bad.Add("в задаче нет элементов"); return bad; }
            if (task.Nodes3.Count == 0) { bad.Add("в задаче нет узлов"); return bad; }

            // ---- 1. Ссылки на узлы, число узлов у типа КЭ, вырожденность ----------
            int badRefs = 0, badArity = 0, degenerate = 0;
            var seenElems = new HashSet<string>();
            int duplicates = 0;

            foreach (var el in task.Elements)
            {
                int vcount = el.Length - 2;
                int expect = el[0] == 10 ? 2 : el[0] == 42 ? 3 : 4;
                if (vcount != expect) badArity++;

                var ids = new List<int>();
                bool refOk = true;
                for (int k = 2; k < el.Length; k++)
                {
                    if (el[k] < 0 || el[k] >= task.Nodes3.Count) { refOk = false; continue; }
                    ids.Add(el[k]);
                }
                if (!refOk) badRefs++;

                var uniq = new HashSet<int>(ids);
                if (uniq.Count != ids.Count) degenerate++;

                ids.Sort();
                if (!seenElems.Add(el[0] + ":" + string.Join(",", ids))) duplicates++;
            }

            if (badRefs > 0) bad.Add($"элементов со ссылкой на несуществующий узел: {badRefs}");
            if (badArity > 0) bad.Add($"элементов с неверным числом узлов для своего типа КЭ: {badArity}");
            if (degenerate > 0) bad.Add($"вырожденных элементов (узел повторён): {degenerate}");
            if (duplicates > 0) bad.Add($"одинаковых элементов: {duplicates}");

            // ---- 2. Жёсткости: использованные обязаны быть объявлены в документе 3 --
            var declared = DeclaredStiffnessIds(task.TaskText);
            var missing = new SortedSet<int>();
            foreach (var el in task.Elements)
                if (!declared.Contains(el[1])) missing.Add(el[1]);
            if (missing.Count > 0)
                bad.Add($"элементы ссылаются на необъявленные жёсткости: {string.Join(", ", missing)}");

            // ---- 3. Структура файла: документы на месте, числа строк сходятся -------
            foreach (var doc in new[] { "( 0/", "( 1/", "( 3/", "( 4/" })
                if (task.TaskText.IndexOf(doc, StringComparison.Ordinal) < 0)
                    bad.Add($"в файле задачи нет документа {doc.Trim()}");

            int elemLines = CountDocumentLines(task.TaskText, "( 1/");
            int nodeLines = CountDocumentLines(task.TaskText, "( 4/");
            if (elemLines != task.Elements.Count)
                bad.Add($"в документе 1 строк {elemLines}, а элементов {task.Elements.Count}");
            if (nodeLines != task.Nodes3.Count)
                bad.Add($"в документе 4 строк {nodeLines}, а узлов {task.Nodes3.Count}");

            // ---- 4. Баланс площадей — недобор означает дыру в схеме ЛИРЫ ------------
            if (task.TargetArea > MeshTol.Zero)
            {
                double rel = Math.Abs(task.SlabArea - task.TargetArea) / task.TargetArea;
                if (rel > MeshTol.AreaBalanceRelTol)
                    bad.Add($"баланс площадей: плита {task.TargetArea * 1e-6:0.###} м², пластины {task.SlabArea * 1e-6:0.###} м² ({rel * 100:0.##}%)");
            }

            return bad;
        }

        // Номера жёсткостей, объявленных в документе 3: строка вида "<номер> GEI ..."
        // или "<номер> S0 ...". Номер 1 (плита) объявляется всегда.
        private HashSet<int> DeclaredStiffnessIds(string taskText)
        {
            var ids = new HashSet<int>();
            foreach (var raw in DocumentLines(taskText, "( 3/"))
            {
                string line = raw.Trim();
                int sp = line.IndexOf(' ');
                if (sp <= 0) continue;
                int id;
                if (!int.TryParse(line.Substring(0, sp), out id)) continue;
                string rest = line.Substring(sp + 1);
                if (rest.StartsWith("GEI", StringComparison.Ordinal) || rest.StartsWith("S0", StringComparison.Ordinal))
                    ids.Add(id);
            }
            return ids;
        }

        // Строки одного документа: от его заголовка до одиночной скобки ")".
        private List<string> DocumentLines(string taskText, string docHeader)
        {
            var result = new List<string>();
            int start = taskText.IndexOf(docHeader, StringComparison.Ordinal);
            if (start < 0) return result;

            var lines = taskText.Substring(start).Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Trim() == ")") break;
                if (line.Length > 0) result.Add(line);
            }
            return result;
        }

        private int CountDocumentLines(string taskText, string docHeader)
        {
            return DocumentLines(taskText, docHeader).Count;
        }

        // Узлы, на которые не ссылается ни один элемент. Не провал: элементы внутри
        // отверстий удаляются уже готовыми, их внутренние узлы остаются в схеме —
        // ЛИРА убирает такие Упаковкой схемы. Число полезно как наблюдение.
        private int UnusedNodeCount(ExportResult task)
        {
            var used = new HashSet<int>();
            foreach (var el in task.Elements)
                for (int k = 2; k < el.Length; k++) used.Add(el[k]);
            return task.Nodes3.Count - used.Count;
        }
    }
}
