using System;

namespace MeshPlugin
{
    // ДОПУСКИ ПЛАГИНА — в одном месте.
    //
    // Это не «магические числа», а правила, по которым плагин решает, что считать
    // одной точкой, одной линией и вырожденным элементом. Разные значения одного и
    // того же допуска в разных функциях — источник самых дорогих багов (узел
    // раздваивается, ребро не находится, элемент теряется), поэтому все они здесь.
    // Единицы — миллиметры чертежа, если не указано иное.
    internal static class MeshTol
    {
        // Две точки ближе этого расстояния — ОДИН узел сетки. Ключевой допуск:
        // от него зависит, сомкнётся ли сетка в узле или останется «открытой».
        public const double NodeMerge = 1e-3;

        // Точка лежит на отрезке (поперечное отклонение от прямой).
        public const double OnSegment = 1e-3;

        // Вырожденный полигон по площади, мм². Элемент меньше — не элемент.
        public const double MinArea = 1e-3;

        // Числовой ноль для длин и координат.
        public const double Zero = 1e-9;

        // Числовой ноль для квадратов длин, определителей и векторных произведений.
        public const double ZeroSq = 1e-12;

        // Кусок отрезка после подрезки короче этого — мусор, не выводится.
        public const double MinPiece = 1.0;

        // Попадание точки на дверной отрезок (косяк, середина куска стены).
        // Заметно грубее OnSegment: дверь чертится вручную поверх оси стены.
        public const double DoorOnAxis = 1.0;

        // Х-пересечение ближе этого к концу отрезка — узловое касание, а не
        // пересечение (такие случаи закрывает SplitSegmentsAtNodes).
        public const double Crossing = 0.5;

        // Вершина, отстоящая от прямой «сосед-сосед» меньше этого, лежит на прямой
        // стороне и убирается: прямоугольник с лишними точками на сторонах снова
        // становится четырёхвершинным.
        public const double Collinear = 0.5;

        // Минимальная сторона конечного элемента. Рёбра короче схлопываются.
        public const double MinElementSize = 100.0;

        // Шаг мелкой сетки внутри отпечатка пилона. Размеры пилонов почти всегда
        // кратны 100, но правило не в кратности: каждая половина стороны (от грани
        // до оси) делится на floor(половина/100) равных частей, поэтому грани и ось
        // всегда остаются линиями сетки, а элемент получается 100–199 мм. Если
        // половина меньше 100 (пилон тоньше 200 мм), деления нет — элемент выходит
        // тоньше MinElementSize, это неизбежная плата за отпечаток тонкого пилона.
        public const double PylonInnerCell = 100.0;

        // Насколько разрешено двигать стены и пилоны к линиям сетки.
        public const double WallSnap = 100.0;

        // Насколько разрешено двигать дверной отрезок вдоль стены. Меньше, чем у
        // стен: сильный сдвиг увёл бы сам проём.
        public const double DoorSnap = 50.0;

        // Сдвиг линии сетки к цели (грань пилона, кромка отверстия, косяк):
        // не более 30% шага и не более 100 мм.
        public const double MaxShiftFactor = 0.3;
        public const double MaxShiftAbs = 100.0;

        // Радиус поиска кандидатов при замыкании открытого узла, в шагах сетки.
        public const double CloseRadiusFactor = 1.6;

        // Допустимое относительное расхождение баланса площадей (сумма площадей
        // элементов плиты против площади контура за вычетом отверстий). Элементы
        // покрывают плиту без щелей и нахлёстов, поэтому расхождение — это либо
        // потерянная грань (дыра в схеме), либо лишний элемент в проёме. Порог не
        // ноль только из-за накопления ошибки округления на десятках тысяч КЭ.
        public const double AreaBalanceRelTol = 0.001;

        // Максимальный сдвиг линии сетки к цели при заданном шаге.
        public static double MaxShift(double step)
        {
            return Math.Min(MaxShiftFactor * step, MaxShiftAbs);
        }

        // Минимальный просвет между соседними линиями сетки после сдвига/вставки
        // линии по цели. Сдвиг, оставляющий полосу уже этого, не выполняется —
        // иначе на месте цели появляется элемент в единицы миллиметров.
        // На мелком шаге правило смягчается, иначе оно запретило бы все сдвиги.
        public static double MinGridGap(double step)
        {
            return Math.Min(MinElementSize, 0.5 * step);
        }
    }

    // РЕЕСТР СЛОЁВ ПЛАГИНА.
    //
    // Имена слоёв — контракт между командами: MESHQUADMESH ищет стены по префиксу,
    // MESHLAYERS по нему же их не трогает, экспорт по нему же читает толщину.
    // Пока префиксы были литералами по всему коду, они успели разойтись
    // ("WALL_DOORS(" против "WALL_DOORS(H-"), и слой то защищался, то нет.
    // Любая проверка слоя — только через функции этого файла.
    public partial class Commands
    {
        // Контур фундаментной плиты: FOUNDATION_SLABS(H-<толщина>)
        private const string SlabLayerPrefix = "FOUNDATION_SLABS(";

        // Стены и оси пилонов: WALLS(H-<толщина>) и WALLS(H-<толщина> PILON)
        private const string WallLayerPrefix = "WALLS(H-";
        private const string PylonMarker = "PILON";

        // Дверные проёмы: WALL_DOORS(H-<высота>). Проверка намеренно широкая (без
        // "H-"): слой без высоты всё равно обязан считаться дверным и защищаться от
        // MESHLAYERS/MESHCLEAN, а высота при разборе имени получает значение по
        // умолчанию.
        private const string DoorLayerPrefix = "WALL_DOORS(";

        // Пилоны-стержни: COLUMNS(SEC-RC_RECT B-.. H-..) и старый общий COLUMNS.
        private const string ColumnLayerName = "COLUMNS";

        // Линии готовой сетки.
        private const string TriangulationLayerName = "LINE_TRIANGULATION";

        // Контуры отверстий/проёмов в плите: внутри сетки нет.
        private const string HoleLayerName = "MESH_HOLES";

        // Контуры пилонов-пластин, сохранённые MESHCOLUMNCROSS. Сам пилон остаётся
        // осью-линией в WALLS(H-... PILON), а контур нужен MESHQUADMESH, чтобы
        // отпечатать его на сетке плиты (узлы в углах, мелкая сетка внутри).
        // В отличие от COLUMNS это НЕ пустота: внутри отпечатка сетка плиты есть.
        private const string PylonOutlineLayerName = "MESH_PYLONS";

        // Обозначение дверного проёма (квадрат в середине проёма) — только для
        // чертежа. Слой обязан быть отдельным от WALL_DOORS(H-...): экспорт читает
        // оттуда любые отрезки, и стороны квадрата стали бы фиктивными дверями.
        private const string DoorMarkLayerName = "WALL_DOORS_MARKS";
        private const double DoorMarkSize = 200.0;

        // Маркеры плагина: MESH_ANGLE_MARKS, MESH_GAP_MARKS, MESH_QUALITY_*.
        private const string MarkLayerPrefix = "MESH_";

        private const string AngleMarkLayerName = "MESH_ANGLE_MARKS";
        private const double AngleMarkRadius = 300.0;

        private const string GapMarkLayerName = "MESH_GAP_MARKS";
        private const double GapMarkRadius = 150.0; // Ø300 мм

        // Слой проблемных мест: места, из-за которых сетка не построилась или
        // построилась с дырами. Общий для MESHQUADMESH и MESHEXPORTTXT — показывает
        // проблемы последнего запуска.
        private const string ProblemLayerName = "ПРОБЛЕМА";
        private const double ProblemMarkRadius = 300.0;

        // Мозаика качества и контуры критических элементов (MESHQUALITY).
        private const string QualityGoodLayerName = "MESH_QUALITY_GOOD"; // зелёный
        private const string QualityMidLayerName = "MESH_QUALITY_MID";   // жёлтый
        private const string QualityBadLayerName = "MESH_QUALITY_BAD";   // красный
        private const string BadElementsLayerName = "ПЛОХИЕ";

        // Стена (в том числе ось пилона).
        private static bool IsWallLayer(string layer)
        {
            return !string.IsNullOrEmpty(layer) && layer.StartsWith(WallLayerPrefix);
        }

        // Ось пилона-пластины: стена с суффиксом PILON.
        private static bool IsPylonLayer(string layer)
        {
            return IsWallLayer(layer) && layer.IndexOf(PylonMarker, StringComparison.Ordinal) >= 0;
        }

        private static bool IsDoorLayer(string layer)
        {
            return !string.IsNullOrEmpty(layer) && layer.StartsWith(DoorLayerPrefix);
        }

        private static bool IsSlabLayer(string layer)
        {
            return !string.IsNullOrEmpty(layer) && layer.StartsWith(SlabLayerPrefix);
        }

        // Пилоны лежат в слоях вида COLUMNS(SEC-RC_RECT B-600 H-300) — по одному
        // слою на типоразмер сечения; старый общий слой COLUMNS тоже распознаётся.
        private static bool IsColumnLayer(string layer)
        {
            return !string.IsNullOrEmpty(layer) && layer.StartsWith(ColumnLayerName);
        }

        // Маркеры и мозаика, созданные плагином.
        private static bool IsMarkLayer(string layer)
        {
            return !string.IsNullOrEmpty(layer)
                && (layer.StartsWith(MarkLayerPrefix) || layer == ProblemLayerName || layer == BadElementsLayerName);
        }

        // Служебные слои плагина: объекты, созданные его же командами, не являются
        // исходными контурами для новых построений (MESHCLEAN их сохраняет,
        // MESHWALLAXIS не принимает за контуры стен).
        private static bool IsServiceLayer(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return false;
            return IsSlabLayer(layer)
                || IsWallLayer(layer)
                || IsDoorLayer(layer)
                || layer == DoorMarkLayerName
                || layer == TriangulationLayerName
                || layer == HoleLayerName
                || layer == PylonOutlineLayerName
                || IsColumnLayer(layer);
        }

        // Толщина стены/высота двери из имени слоя: "...(H-250)" -> 250.
        // Разделитель дробной части в имени слоя может быть и точкой, и запятой.
        private static readonly System.Text.RegularExpressions.Regex LayerHeightRegex =
            new System.Text.RegularExpressions.Regex(@"H-([\d.,]+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex ColumnDimsRegex =
            new System.Text.RegularExpressions.Regex(@"B-([\d.,]+)\s+H-([\d.,]+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex SlabThicknessRegex =
            new System.Text.RegularExpressions.Regex(@"FOUNDATION_SLABS\(H-([\d.,]+)\)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Число из имени слоя: точка и запятая — одинаково допустимый разделитель,
        // разбор всегда инвариантный (локаль AutoCAD на имена слоёв не влияет).
        private static double ParseLayerNumber(string s)
        {
            return double.Parse(s.Replace(',', '.'),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool TryParseLayerHeight(string layer, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(layer)) return false;
            var m = LayerHeightRegex.Match(layer);
            if (!m.Success) return false;
            value = ParseLayerNumber(m.Groups[1].Value);
            return true;
        }
    }
}
