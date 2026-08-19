using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MeshPlugin
{
    // САМОТЕСТ ПЛАГИНА (MESHSELFTEST).
    //
    // Ошибки сетки находились до сих пор самым дорогим способом: на реальном плане,
    // по одной, кругом «закрыть AutoCAD → собрать → открыть → посмотреть». Причём
    // ломается сетка обычно не на простом прямоугольнике, а на редкой форме —
    // узком выступе, пилоне у самого края, проёме вплотную к стене. На текущем
    // чертеже такой формы может не быть, а на следующем объекте она будет.
    //
    // Самотест прогоняет расчётное ядро (BuildMeshCore) на десятках ВЫДУМАННЫХ
    // планов и проверяет каждый результат теми же постусловиями, что и обычное
    // построение. К чертежу пользователя он отношения не имеет: планы живут в
    // памяти, ничего не рисуется и не сохраняется. На выходе одно число — сколько
    // прогонов провалилось; провалившийся план воспроизводится по номеру (seed)
    // командой MESHSELFTESTCASE.
    //
    // Оракул — не «эталонная сетка» (её неоткуда взять), а правила, которые сетка
    // обязана соблюдать при ЛЮБОМ входе: не выходить за контур, не залезать в
    // пустоту, не иметь совпадающих рёбер и пересечений без узла. Плюс правило,
    // которого в постусловиях нет: тот же план, сдвинутый в другое место чертежа,
    // обязан дать ту же сетку. Оно ловит завязки на абсолютные координаты — самый
    // частый класс ошибок в сеточном коде.

    // Выдуманный план: то же, что плагин прочитал бы со слоёв чертежа.
    internal class TestPlan
    {
        public int Seed;
        public string Shape = "";
        public double CellSize = 300.0;
        public List<Point2d> Contour = new List<Point2d>();
        public List<Point2d[]> Walls = new List<Point2d[]>();
        public List<List<Point2d>> Columns = new List<List<Point2d>>();  // COLUMNS: пустота
        public List<List<Point2d>> Holes = new List<List<Point2d>>();    // MESH_HOLES: пустота
        public List<List<Point2d>> Pylons = new List<List<Point2d>>();   // MESH_PYLONS: мелкая сетка

        public string Describe()
        {
            double w = 0, h = 0;
            if (Contour.Count > 0)
            {
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var p in Contour)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }
                w = maxX - minX;
                h = maxY - minY;
            }
            return $"{Shape} {w:0}×{h:0}, шаг {CellSize:0}, стен {Walls.Count}, пилонов-стержней {Columns.Count}, отверстий {Holes.Count}, пилонов-пластин {Pylons.Count}";
        }
    }

    public partial class Commands
    {
        // Планов по умолчанию за прогон. Полсотни укладываются в минуту-две и уже
        // перебирают все формы контура на всех шагах сетки; больше имеет смысл
        // гонять после правок в самом алгоритме.
        private const int SelfTestDefaultCount = 50;

        // Куда переносится план в проверке на сдвиг. Числа НЕ кратны ни одному из
        // шагов сетки: кратный сдвиг совпал бы с шагом и проверку бы не нагрузил.
        private const double SelfTestShiftX = 13137.0;
        private const double SelfTestShiftY = -7351.0;

        [CommandMethod("MESHSELFTEST")]
        public void SelfTestCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHSELFTEST");

            PromptIntegerOptions pio = new PromptIntegerOptions(
                $"\nСколько выдуманных планов проверить (Enter — {SelfTestDefaultCount}): ");
            pio.DefaultValue = SelfTestDefaultCount;
            pio.AllowNegative = false;
            pio.AllowZero = false;
            PromptIntegerResult pir = ed.GetInteger(pio);
            if (pir.Status != PromptStatus.OK && pir.Status != PromptStatus.None) return;
            int count = pir.Status == PromptStatus.OK ? pir.Value : SelfTestDefaultCount;

            PromptIntegerOptions pioSeed = new PromptIntegerOptions("\nС какого номера плана начать (Enter — 1): ");
            pioSeed.DefaultValue = 1;
            pioSeed.AllowNegative = false;
            pioSeed.AllowZero = false;
            PromptIntegerResult pirSeed = ed.GetInteger(pioSeed);
            if (pirSeed.Status != PromptStatus.OK && pirSeed.Status != PromptStatus.None) return;
            int firstSeed = pirSeed.Status == PromptStatus.OK ? pirSeed.Value : 1;

            ed.WriteMessage($"\nПрогон {count} планов, номера {firstSeed}–{firstSeed + count - 1}. Чертёж не изменяется.\n");

            var failures = new List<string>();
            int shortEdgeCases = 0;      // не провал: у пилона тоньше 200 мм это норма
            int rotationChecked = 0, rotationSame = 0;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < count; i++)
            {
                int seed = firstSeed + i;
                var bad = new List<string>();
                TestPlan plan = null;
                MeshResult res = null;

                try
                {
                    plan = MakeTestPlan(seed);
                    res = BuildMeshCore(PlanToInput(plan, 0.0, 0.0, false));
                    bad.AddRange(CheckMeshInvariants(res));
                    if (res.ShortEdges > 0) shortEdgeCases++;
                }
                catch (System.Exception ex)
                {
                    failures.Add($"план {seed}: ИСКЛЮЧЕНИЕ {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                // Тот же план в другом месте чертежа — сетка обязана быть той же.
                // Сравниваются числа, а не координаты: сами линии сдвинуты.
                try
                {
                    var moved = BuildMeshCore(PlanToInput(plan, SelfTestShiftX, SelfTestShiftY, false));
                    if (moved.Segments.Count != res.Segments.Count)
                        bad.Add($"сдвиг плана меняет сетку: рёбер было {res.Segments.Count}, стало {moved.Segments.Count}");
                    else if (moved.ProblemPts.Count != res.ProblemPts.Count)
                        bad.Add($"сдвиг плана меняет число проблемных мест: было {res.ProblemPts.Count}, стало {moved.ProblemPts.Count}");
                }
                catch (System.Exception ex)
                {
                    bad.Add($"сдвиг плана: ИСКЛЮЧЕНИЕ {ex.GetType().Name}: {ex.Message}");
                }

                // Поворот на 90° пока только НАБЛЮДАЕТСЯ, а не считается правилом:
                // объединение треугольников в четырёхугольники идёт жадно, по порядку
                // их появления, и поворот этот порядок меняет — расхождение может
                // оказаться законным. Сначала посмотрим на числа, потом решим,
                // делать ли из этого правило.
                try
                {
                    var turned = BuildMeshCore(PlanToInput(plan, 0.0, 0.0, true));
                    rotationChecked++;
                    if (turned.Segments.Count == res.Segments.Count) rotationSame++;
                }
                catch (System.Exception)
                {
                    // молча: наблюдение, а не проверка
                }

                if (bad.Count > 0)
                    failures.Add($"план {seed} ({plan.Describe()}): {string.Join("; ", bad)}");

                if ((i + 1) % 10 == 0)
                    ed.WriteMessage($"  проверено {i + 1} из {count}, провалов {failures.Count}\n");
            }

            sw.Stop();

            ed.WriteMessage($"\nСАМОТЕСТ: прогонов {count}, провалов {failures.Count}, время {sw.Elapsed.TotalSeconds:0} с\n");

            if (failures.Count == 0)
            {
                ed.WriteMessage("Все проверенные планы отвечают жёстким правилам сетки.\n");
            }
            else
            {
                ed.WriteMessage("Провалы:\n");
                foreach (var f in failures) ed.WriteMessage("  " + f + "\n");
                ed.WriteMessage("Посмотреть провалившийся план: команда MESHSELFTESTCASE, ввести его номер — план начертится в текущем чертеже, дальше обычный MESHQUADMESH.\n");
            }

            if (shortEdgeCases > 0)
                ed.WriteMessage($"Не провал, к сведению: рёбра короче {MeshTol.MinElementSize:0} мм встретились в {shortEdgeCases} планах (обычная причина — пилон тоньше {2 * MeshTol.PylonInnerCell:0} мм).\n");

            if (rotationChecked > 0)
                ed.WriteMessage($"Наблюдение (не проверка): поворот плана на 90° дал столько же рёбер в {rotationSame} случаях из {rotationChecked}.\n");
        }

        // Начертить выдуманный план по его номеру, чтобы посмотреть на него глазами
        // и прогнать обычный MESHQUADMESH. Слои — те же, что плагин ждёт от чертежа.
        [CommandMethod("MESHSELFTESTCASE")]
        public void SelfTestCaseCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "MESHSELFTESTCASE");
            Database db = doc.Database;

            PromptIntegerOptions pio = new PromptIntegerOptions("\nНомер плана (seed) из отчёта самотеста: ");
            pio.AllowNegative = false;
            pio.AllowZero = false;
            PromptIntegerResult pir = ed.GetInteger(pio);
            if (pir.Status != PromptStatus.OK) return;

            TestPlan plan = MakeTestPlan(pir.Value);
            ed.WriteMessage($"\nПлан {plan.Seed}: {plan.Describe()}\n");

            var res = BuildMeshCore(PlanToInput(plan, 0.0, 0.0, false));
            var bad = CheckMeshInvariants(res);
            ed.WriteMessage(bad.Count == 0
                ? "Расчёт по этому плану правил не нарушает.\n"
                : $"Нарушено: {string.Join("; ", bad)}\n");

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DrawTestPlan(tr, db, plan);
                    tr.Commit();
                }
                ed.WriteMessage($"План начерчен. Дальше: MESHQUADMESH, контур плиты, шаг {plan.CellSize:0}.\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка MESHSELFTESTCASE: {ex.Message}\nЧертёж не изменён.\n");
            }
        }

        // ---- ПРОВЕРКА РЕЗУЛЬТАТА -------------------------------------------------

        // Правила, которые сетка обязана соблюдать при любом входе. Считает их не
        // самотест — их посчитало само построение (SelfCheck.cs); здесь только
        // вердикт. Так проверка и построение не разъезжаются.
        private List<string> CheckMeshInvariants(MeshResult res)
        {
            var bad = new List<string>();

            if (!res.Ok)
            {
                // Ядро остановилось на жёстком правиле входа. Для выдуманного плана
                // это ошибка ГЕНЕРАТОРА (он обязан ставить объекты внутрь плиты),
                // но молчать о ней нельзя: та же ветка срабатывает и на ошибке
                // самой проверки «объект внутри контура».
                bad.Add("построение остановлено на жёстком правиле входа");
                return bad;
            }

            if (res.Segments.Count == 0) bad.Add("сетка пустая");
            if (res.OutsideContour > 0) bad.Add($"рёбер вне контура плиты: {res.OutsideContour}");
            if (res.InsideVoid > 0) bad.Add($"рёбер внутри пилонов/проёмов: {res.InsideVoid}");
            if (res.DuplicateEdges > 0) bad.Add($"совпадающих рёбер: {res.DuplicateEdges}");
            if (res.DegenerateEdges > 0) bad.Add($"вырожденных рёбер: {res.DegenerateEdges}");
            if (res.CrossingsLeft > 0) bad.Add($"пересечений линий без узла: {res.CrossingsLeft}");
            if (res.UnlinkedPylonNodes > 0) bad.Add($"узлов контура пилона вне сетки: {res.UnlinkedPylonNodes}");
            if (res.FailedPolygons > 0) bad.Add($"не удалось триангулировать полигонов: {res.FailedPolygons}");

            return bad;
        }

        // ---- ГЕНЕРАТОР ПЛАНОВ ----------------------------------------------------

        // План строится по номеру (seed) и только по нему: один и тот же номер
        // всегда даёт один и тот же план. Иначе провал самотеста было бы нечем
        // воспроизвести — а именно ради воспроизведения он и нужен.
        private TestPlan MakeTestPlan(int seed)
        {
            var rnd = new Random(seed);
            var plan = new TestPlan { Seed = seed };

            double[] steps = { 200.0, 300.0, 400.0, 500.0 };
            plan.CellSize = steps[rnd.Next(steps.Length)];

            // Размеры кратны 100 мм — как на реальном плане.
            double w = 100.0 * rnd.Next(60, 200);   // 6.0–19.9 м
            double h = 100.0 * rnd.Next(60, 200);

            int kind = rnd.Next(3);
            if (kind == 0)
            {
                plan.Shape = "прямоугольник";
                plan.Contour = RectPoly(0, 0, w, h);
            }
            else if (kind == 1)
            {
                // Г-образная плита: вырез в правом верхнем углу.
                plan.Shape = "Г-образная";
                double cw = 100.0 * rnd.Next(15, (int)(w / 200.0));
                double ch = 100.0 * rnd.Next(15, (int)(h / 200.0));
                plan.Contour = new List<Point2d>
                {
                    new Point2d(0, 0),
                    new Point2d(w, 0),
                    new Point2d(w, h - ch),
                    new Point2d(w - cw, h - ch),
                    new Point2d(w - cw, h),
                    new Point2d(0, h)
                };
            }
            else
            {
                // П-образная плита: вырез сверху посередине. Даёт два узких выступа —
                // самое неудобное место для сетки.
                plan.Shape = "П-образная";
                double cw = 100.0 * rnd.Next(10, (int)(w / 300.0) + 11);
                double ch = 100.0 * rnd.Next(10, (int)(h / 200.0));
                double x0 = Math.Round((w - cw) / 2.0 / 100.0) * 100.0;
                plan.Contour = new List<Point2d>
                {
                    new Point2d(0, 0),
                    new Point2d(w, 0),
                    new Point2d(w, h),
                    new Point2d(x0 + cw, h),
                    new Point2d(x0 + cw, h - ch),
                    new Point2d(x0, h - ch),
                    new Point2d(x0, h),
                    new Point2d(0, h)
                };
            }

            EnsureCcw(plan.Contour);

            // Занятые места: пустоты и отпечатки не должны налезать друг на друга —
            // на реальном плане они тоже не налезают, а проверять надо алгоритм
            // сетки, а не его поведение на заведомо кривом чертеже.
            var taken = new List<double[]>();

            int holes = rnd.Next(0, 4);
            for (int i = 0; i < holes; i++)
            {
                var r = PlaceRect(rnd, plan.Contour, taken, 600, 2500, 600, 2500, plan.CellSize);
                if (r != null) plan.Holes.Add(r);
            }

            int columns = rnd.Next(0, 4);
            for (int i = 0; i < columns; i++)
            {
                var r = PlaceRect(rnd, plan.Contour, taken, 400, 900, 400, 900, plan.CellSize);
                if (r != null) plan.Columns.Add(r);
            }

            int pylons = rnd.Next(0, 4);
            for (int i = 0; i < pylons; i++)
            {
                var r = PlaceRect(rnd, plan.Contour, taken, 200, 600, 800, 2000, plan.CellSize);
                if (r != null) plan.Pylons.Add(r);
            }

            int walls = rnd.Next(0, 7);
            for (int i = 0; i < walls; i++)
            {
                var seg = PlaceWall(rnd, plan.Contour, taken);
                if (seg != null) plan.Walls.Add(seg);
            }

            return plan;
        }

        private List<Point2d> RectPoly(double x, double y, double w, double h)
        {
            return new List<Point2d>
            {
                new Point2d(x, y),
                new Point2d(x + w, y),
                new Point2d(x + w, y + h),
                new Point2d(x, y + h)
            };
        }

        // Прямоугольник случайного размера в случайном месте плиты: целиком внутри
        // контура и не ближе зазора к уже поставленным. Не влезло за 20 попыток —
        // объекта просто не будет (плита бывает и тесная).
        private List<Point2d> PlaceRect(
            Random rnd, List<Point2d> contour, List<double[]> taken,
            int minW, int maxW, int minH, int maxH, double gap)
        {
            var bb = PolygonBBox(contour);

            for (int attempt = 0; attempt < 20; attempt++)
            {
                double w = 100.0 * rnd.Next(minW / 100, maxW / 100 + 1);
                double h = 100.0 * rnd.Next(minH / 100, maxH / 100 + 1);
                if (rnd.Next(2) == 0) { double t = w; w = h; h = t; }   // и «лежащие», и «стоящие»

                if (bb[2] - bb[0] <= w || bb[3] - bb[1] <= h) continue;

                double x = bb[0] + 100.0 * rnd.Next(0, (int)((bb[2] - bb[0] - w) / 100.0) + 1);
                double y = bb[1] + 100.0 * rnd.Next(0, (int)((bb[3] - bb[1] - h) / 100.0) + 1);

                double[] box = { x, y, x + w, y + h };
                if (BoxOverlapsAny(box, taken, gap)) continue;

                var poly = RectPoly(x, y, w, h);
                if (!IsPolygonInsideContour(poly, contour)) continue;

                taken.Add(box);
                return poly;
            }
            return null;
        }

        // Отрезок стены: по горизонтали или вертикали, целиком внутри плиты и мимо
        // пустот (на чертеже стена сквозь пилон тоже не проходит).
        private Point2d[] PlaceWall(Random rnd, List<Point2d> contour, List<double[]> taken)
        {
            var bb = PolygonBBox(contour);

            for (int attempt = 0; attempt < 20; attempt++)
            {
                double len = 100.0 * rnd.Next(20, 60);
                double x = bb[0] + 100.0 * rnd.Next(0, (int)((bb[2] - bb[0]) / 100.0) + 1);
                double y = bb[1] + 100.0 * rnd.Next(0, (int)((bb[3] - bb[1]) / 100.0) + 1);

                Point2d a = new Point2d(x, y);
                Point2d b = rnd.Next(2) == 0 ? new Point2d(x + len, y) : new Point2d(x, y + len);

                double[] box =
                {
                    Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                    Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)
                };
                if (BoxOverlapsAny(box, taken, 0.0)) continue;
                if (!IsSegmentInsideContour(a, b, contour)) continue;

                taken.Add(box);
                return new Point2d[] { a, b };
            }
            return null;
        }

        private bool BoxOverlapsAny(double[] box, List<double[]> taken, double gap)
        {
            foreach (var t in taken)
            {
                if (box[2] + gap < t[0] || box[0] - gap > t[2]) continue;
                if (box[3] + gap < t[1] || box[1] - gap > t[3]) continue;
                return true;
            }
            return false;
        }

        // ---- ПЛАН -> ВХОД РАСЧЁТА ------------------------------------------------

        // Тот же план на другом месте чертежа (сдвиг) или повёрнутый на 90°.
        // Поворот (x, y) -> (-y, x) сохраняет обход контура против часовой стрелки.
        private MeshInput PlanToInput(TestPlan plan, double dx, double dy, bool rotate)
        {
            var input = new MeshInput
            {
                CellSize = plan.CellSize,
                Contour = MovePoly(plan.Contour, dx, dy, rotate),
                SnapDoors = null                    // дверей в выдуманном плане нет
            };

            EnsureCcw(input.Contour);

            foreach (var w in plan.Walls)
                input.WallSegments.Add(new Point2d[] { MovePt(w[0], dx, dy, rotate), MovePt(w[1], dx, dy, rotate) });

            foreach (var c in plan.Columns)
            {
                var poly = MovePoly(c, dx, dy, rotate);
                EnsureCcw(poly);
                input.ColumnPolys.Add(poly);
            }

            foreach (var h in plan.Holes)
            {
                var poly = MovePoly(h, dx, dy, rotate);
                EnsureCcw(poly);
                input.HolePolys.Add(poly);
            }

            // Отпечаток пилона читается кодом как ПРЯМОУГОЛЬНИК: стороны берутся по
            // вершинам 0 и 2, поэтому порядок обхода менять нельзя — иначе противо-
            // положный угол окажется не там. Отсюда и отдельная ветка без EnsureCcw.
            foreach (var r in plan.Pylons)
                input.PylonRects.Add(MovePoly(r, dx, dy, rotate));

            SnapPlanToGrid(input);
            return input;
        }

        // Тот же снап к линиям сетки, который перед расчётом делает команда: стены и
        // пилоны-стержни подтягиваются к сетке, если до неё меньше WallSnapTolerance.
        // Без этого самотест кормил бы ядро тем, чего в реальном конвейере не бывает,
        // и его провалы ничего не говорили бы о работе на настоящем чертеже.
        // Отверстия и отпечатки пилонов не двигаются — их не двигает и команда
        // (кромка отверстия и так жёсткая цель выравнивания линий сетки).
        // Сдвиг считается от угла габарита контура, поэтому весь план целиком можно
        // перенести куда угодно — снап получится тот же.
        private void SnapPlanToGrid(MeshInput input)
        {
            var bb = PolygonBBox(input.Contour);
            double minX = bb[0], minY = bb[1], cell = input.CellSize;

            foreach (var w in input.WallSegments)
            {
                w[0] = new Point2d(SnapCoord(w[0].X, minX, cell), SnapCoord(w[0].Y, minY, cell));
                w[1] = new Point2d(SnapCoord(w[1].X, minX, cell), SnapCoord(w[1].Y, minY, cell));
            }

            // Пилон-стержень двигается ЦЕЛИКОМ: к сетке подтягивается его нижний левый
            // угол, остальные едут за ним — иначе контур перестал бы быть прямоугольным.
            foreach (var col in input.ColumnPolys)
            {
                var cb = PolygonBBox(col);
                double dxc = SnapCoord(cb[0], minX, cell) - cb[0];
                double dyc = SnapCoord(cb[1], minY, cell) - cb[1];
                if (Math.Abs(dxc) < MeshTol.Zero && Math.Abs(dyc) < MeshTol.Zero) continue;

                for (int i = 0; i < col.Count; i++)
                    col[i] = new Point2d(col[i].X + dxc, col[i].Y + dyc);
            }
        }

        private Point2d MovePt(Point2d p, double dx, double dy, bool rotate)
        {
            return rotate
                ? new Point2d(-p.Y + dx, p.X + dy)
                : new Point2d(p.X + dx, p.Y + dy);
        }

        private List<Point2d> MovePoly(List<Point2d> poly, double dx, double dy, bool rotate)
        {
            var res = new List<Point2d>(poly.Count);
            foreach (var p in poly) res.Add(MovePt(p, dx, dy, rotate));
            return res;
        }

        // ---- ОТРИСОВКА ПЛАНА -----------------------------------------------------

        private void DrawTestPlan(Transaction tr, Database db, TestPlan plan)
        {
            const string slabLayer = "FOUNDATION_SLABS(H-300)";
            const string wallLayer = "WALLS(H-200)";

            EnsureLayer(db, tr, slabLayer, 7);
            EnsureLayer(db, tr, wallLayer, 3);
            EnsureLayer(db, tr, ColumnLayerName, 1);
            EnsureLayer(db, tr, HoleLayerName, 4);
            EnsureLayer(db, tr, PylonOutlineLayerName, 6);

            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

            AddClosedPolyline(tr, ms, plan.Contour, slabLayer);
            foreach (var c in plan.Columns) AddClosedPolyline(tr, ms, c, ColumnLayerName);
            foreach (var h in plan.Holes) AddClosedPolyline(tr, ms, h, HoleLayerName);
            foreach (var r in plan.Pylons) AddClosedPolyline(tr, ms, r, PylonOutlineLayerName);

            foreach (var w in plan.Walls)
            {
                Line ln = new Line(new Point3d(w[0].X, w[0].Y, 0), new Point3d(w[1].X, w[1].Y, 0));
                ln.Layer = wallLayer;
                ms.AppendEntity(ln);
                tr.AddNewlyCreatedDBObject(ln, true);
            }
        }

        private void AddClosedPolyline(Transaction tr, BlockTableRecord ms, List<Point2d> pts, string layer)
        {
            Polyline pl = new Polyline();
            for (int i = 0; i < pts.Count; i++)
                pl.AddVertexAt(i, pts[i], 0, 0, 0);
            pl.Closed = true;
            pl.Layer = layer;
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
        }
    }
}
