using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    public partial class Commands
    {
        // ПОСТУСЛОВИЯ (ИНВАРИАНТЫ) — проверка результата на выходе конвейера.
        //
        // Каждый алгоритм плагина обязан оставить после себя геометрию, отвечающую
        // нескольким простым правилам. Раньше эти правила существовали только в
        // комментариях и проверялись глазами по чертежу или парсером готового файла
        // ЛИРЫ — так были найдены «дырки под стенами», «веер КЭ 42 в проёмах» и
        // «осиротевшие круги ПРОБЛЕМА», причём каждый раз с задержкой в несколько
        // прогонов. Здесь те же правила посчитаны кодом и выводятся в консоль сразу:
        // ненулевой счётчик — сигнал, что этап конвейера не отработал.
        //
        // Главный из инвариантов — БАЛАНС ПЛОЩАДЕЙ: сумма площадей элементов плиты
        // обязана равняться площади контура за вычетом отверстий. Элементы плиты
        // покрывают её без нахлёстов и щелей, поэтому любая потерянная грань
        // (дыра в схеме ЛИРЫ) и любой лишний элемент в проёме сразу видны числом.

        // Площадь, которую сетка ОБЯЗАНА покрыть: контур минус отверстия, мм².
        // Отверстия считаются непересекающимися (это гарантирует правило «отверстие
        // целиком внутри плиты» + здравый смысл чертежа); наложение двух проёмов
        // друг на друга вычло бы общую часть дважды и дало бы ложный перебор.
        private double SlabTargetArea(
            List<Point2d> contourPts,
            List<List<Point2d>> holePolys,
            out double holesArea)
        {
            holesArea = 0.0;
            if (holePolys != null)
                foreach (var hp in holePolys)
                    if (hp != null && hp.Count >= 3) holesArea += Math.Abs(PolygonArea(hp));

            return Math.Abs(PolygonArea(contourPts)) - holesArea;
        }

        // Площадь одного КЭ в плане по его 3D-узлам: {тип, № жёсткости, узлы...}.
        // ВАЖНО: четырёхугольник записан для ЛИРЫ «змейкой» (face0 face1 face3 face2),
        // поэтому обходить его как многоугольник надо в порядке 0,1,3,2 — иначе
        // получится самопересекающаяся «бабочка» и площадь будет неверной.
        // Стержень (КЭ 10) и вертикальная стена площади в плане не дают.
        private double ElementPlanArea(int[] el, List<double[]> nodes3)
        {
            int vcount = el.Length - 2;
            if (vcount < 3) return 0.0;

            var poly = new List<Point2d>(vcount);
            if (vcount == 4)
            {
                int[] order = { 2, 3, 5, 4 };
                foreach (int k in order)
                    poly.Add(new Point2d(nodes3[el[k]][0], nodes3[el[k]][1]));
            }
            else
            {
                for (int k = 2; k < el.Length; k++)
                    poly.Add(new Point2d(nodes3[el[k]][0], nodes3[el[k]][1]));
            }

            return Math.Abs(PolygonArea(poly));
        }

        // Площадь, покрытая пластинами (оценка качества строит их как Point2d[]).
        private double PlatesArea(List<Point2d[]> plates)
        {
            double sum = 0.0;
            var poly = new List<Point2d>(4);
            foreach (var pl in plates)
            {
                if (pl == null || pl.Length < 3) continue;
                poly.Clear();
                foreach (var p in pl) poly.Add(p);
                sum += Math.Abs(PolygonArea(poly));
            }
            return sum;
        }

        // Сверка баланса площадей и вывод результата. Площади внутри плагина — в мм²,
        // пользователю показываются в м² (как в ЛИРЕ).
        private void ReportAreaBalance(
            Editor ed,
            double meshArea,
            double targetArea,
            double holesArea,
            int plateCount)
        {
            const double Mm2ToM2 = 1e-6;
            double target = targetArea;
            double contourArea = targetArea + holesArea;
            if (target <= MeshTol.Zero)
            {
                ed.WriteMessage("\nБаланс площадей: площадь контура за вычетом отверстий не положительна — проверка пропущена.\n");
                return;
            }

            double diff = meshArea - target;           // > 0 — залито лишнее, < 0 — недобор
            double rel = Math.Abs(diff) / target;

            ed.WriteMessage(
                $"\nБаланс площадей: контур {contourArea * Mm2ToM2:0.###} м²" +
                (holesArea > 0 ? $" − отверстия {holesArea * Mm2ToM2:0.###} м²" : "") +
                $" = {target * Mm2ToM2:0.###} м²; элементов {plateCount}, их площадь {meshArea * Mm2ToM2:0.###} м²; " +
                $"расхождение {diff * Mm2ToM2:+0.###;-0.###;0} м² ({rel * 100:0.###}%)");

            if (rel <= MeshTol.AreaBalanceRelTol)
            {
                ed.WriteMessage(" — норма.\n");
                return;
            }

            if (diff < 0)
                ed.WriteMessage($"\nВНИМАНИЕ: элементы не покрывают {Math.Abs(diff) * Mm2ToM2:0.###} м² плиты — в схеме ЛИРЫ там будут дыры. Смотрите круги в слое {ProblemLayerName} и сообщения о потерянных гранях, затем перестройте сетку.\n");
            else
                ed.WriteMessage($"\nВНИМАНИЕ: элементы покрывают на {diff * Mm2ToM2:0.###} м² больше площади плиты — вероятно, залит проём (контур ушёл со слоя {HoleLayerName}) или элементы наложились друг на друга.\n");
        }

        // Прямоугольник охвата полигона — предфильтр для проверок «точка внутри пустоты»
        // на планах с десятками пилонов.
        private double[] PolygonBBox(List<Point2d> poly)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in poly)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            return new double[] { minX, minY, maxX, maxY };
        }

        private bool IsOnPolygonBoundary(Point2d p, List<Point2d> poly, double eps)
        {
            int n = poly.Count;
            for (int i = 0; i < n; i++)
                if (IsPointOnSegment(p, poly[i], poly[(i + 1) % n], eps)) return true;
            return false;
        }

        // ПОСТУСЛОВИЯ ГОТОВОЙ СЕТКИ (MESHQUADMESH), проверяются на итоговых отрезках
        // перед отрисовкой. Проверяются ровно те правила, которые конвейер обязан был
        // выполнить своими этапами:
        //   1. ничего не выходит за контур плиты      (ClipSegmentsToContour);
        //   2. внутренность пилона и проёма пуста     (ClipSegmentsOutsideColumns);
        //   3. нет рёбер короче MinElementSize        (WeldShortNodes);
        //   4. нет совпадающих и вырожденных рёбер    (DeduplicateSegments).
        // Проверка ничего не исправляет и не меняет чертёж — только считает и
        // печатает: молчаливое «исправление» скрыло бы сбой этапа.
        private void RunMeshSelfCheck(
            Editor ed,
            List<Point2d[]> segments,
            List<Point2d> contourPts,
            List<List<Point2d>> voidPolys)
        {
            int outsideContour = 0, insideVoid = 0, shortEdges = 0, duplicates = 0, degenerate = 0;
            double worstShort = double.MaxValue;
            Point2d worstShortPt = Point2d.Origin;
            var samples = new List<string>();

            var voidBoxes = new List<double[]>();
            if (voidPolys != null)
                foreach (var v in voidPolys) voidBoxes.Add(PolygonBBox(v));

            var ni = new NodeIndex();
            var seen = new HashSet<long>();

            foreach (var seg in segments)
            {
                double len = seg[0].GetDistanceTo(seg[1]);
                if (len < MeshTol.NodeMerge) { degenerate++; continue; }

                int ia = ni.GetNode(seg[0]);
                int ib = ni.GetNode(seg[1]);
                if (!seen.Add(EdgePairKey(ia, ib))) duplicates++;

                if (len < MeshTol.MinElementSize - MeshTol.OnSegment)
                {
                    shortEdges++;
                    if (len < worstShort)
                    {
                        worstShort = len;
                        worstShortPt = new Point2d((seg[0].X + seg[1].X) / 2.0, (seg[0].Y + seg[1].Y) / 2.0);
                    }
                }

                Point2d mid = new Point2d((seg[0].X + seg[1].X) / 2.0, (seg[0].Y + seg[1].Y) / 2.0);

                // Отрезок вдоль стороны контура лежит на границе: середина попадает то
                // внутрь, то наружу по прихоти лучевого теста — такие не считаем.
                if (!IsPointInPolygon(mid, contourPts) && !IsOnPolygonBoundary(mid, contourPts, MeshTol.MinPiece))
                {
                    outsideContour++;
                    if (samples.Count < 3) samples.Add($"({mid.X:0}, {mid.Y:0}) вне контура");
                }

                for (int v = 0; v < voidBoxes.Count; v++)
                {
                    double[] b = voidBoxes[v];
                    if (mid.X < b[0] || mid.X > b[2] || mid.Y < b[1] || mid.Y > b[3]) continue;
                    if (!IsPointInPolygon(mid, voidPolys[v])) continue;
                    if (IsOnPolygonBoundary(mid, voidPolys[v], MeshTol.MinPiece)) continue;
                    insideVoid++;
                    if (samples.Count < 3) samples.Add($"({mid.X:0}, {mid.Y:0}) внутри пустоты");
                    break;
                }
            }

            ed.WriteMessage(
                $"\nСамопроверка сетки: рёбер {segments.Count}; вне контура плиты: {outsideContour}, " +
                $"внутри пилонов/проёмов: {insideVoid}, короче {MeshTol.MinElementSize:0} мм: {shortEdges}, " +
                $"совпадающих: {duplicates}, вырожденных: {degenerate}\n");

            if (outsideContour + insideVoid > 0)
                ed.WriteMessage($"ВНИМАНИЕ: нарушены жёсткие правила обрезки сетки{(samples.Count > 0 ? ", например: " + string.Join("; ", samples) : "")} — сообщите об этом, это ошибка плагина, а не чертежа.\n");
            if (shortEdges > 0)
                ed.WriteMessage($"ВНИМАНИЕ: рёбер короче {MeshTol.MinElementSize:0} мм: {shortEdges}, самое короткое {worstShort:0.#} мм у точки ({worstShortPt.X:0}, {worstShortPt.Y:0}) — в этих местах КЭ вырожденные.\n");
            if (duplicates + degenerate > 0)
                ed.WriteMessage($"ВНИМАНИЕ: совпадающих рёбер: {duplicates}, вырожденных: {degenerate} — в ЛИРЕ это наложенные элементы.\n");
        }
    }
}
