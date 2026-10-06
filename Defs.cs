using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

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

        // Своего шага у сетки внутри отпечатка пилона больше нет (06.10.2026):
        // внутренность строится по линиям сетки ПЛИТЫ, попавшим внутрь, плюс грани и
        // ось (BuildPylonInnerCoords). Отсюда и плотность: какова сетка плиты, такова
        // и внутри пилона. Линия ближе MinElementSize к грани или оси не берётся —
        // она дала бы лепесток тоньше минимального элемента.
        //
        // У пилона тоньше 2 x MinElementSize элемент всё равно выходит тоньше
        // минимального (половина толщины и есть его ширина) — неизбежная плата за
        // отпечаток тонкого пилона, построение про это честно пишет.

        // Насколько разрешено двигать стены и пилоны к линиям сетки.
        public const double WallSnap = 100.0;

        // Насколько разрешено двигать дверной отрезок вдоль стены. Меньше, чем у
        // стен: сильный сдвиг увёл бы сам проём.
        public const double DoorSnap = 50.0;

        // Сдвиг линии сетки к цели (грань пилона, кромка отверстия, косяк):
        // не более 30% шага и не более 100 мм.
        public const double MaxShiftFactor = 0.3;
        public const double MaxShiftAbs = 100.0;

        // Насколько ячейка вправе оказаться крупнее заданного шага. Сдвиг линии к
        // цели растягивает соседнюю полосу на величину сдвига (до 100 мм), а удаление
        // линии ради жёсткой цели — сразу почти вдвое. Пользователь задал шаг и вправе
        // ожидать, что ячейка не вырастет сверх него заметно, поэтому полоса шире
        // MaxCellFactor x шаг делится на равные части (BuildGridCoords).
        public const double MaxCellFactor = 1.1;

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

    // Точка проблемы вместе с коротким объяснением. Круг без подписи заставляет
    // инженера лезть в консоль и гадать, что именно не так в этом месте, — поэтому
    // причина едет вместе с координатой от места, где она обнаружена, до чертежа.
    internal struct ProblemMark
    {
        public Point2d Pt;
        public string Text;

        public ProblemMark(Point2d pt, string text) { Pt = pt; Text = text; }

        // Пометить готовый список точек одной и той же подписью.
        public static List<ProblemMark> From(List<Point2d> pts, string text)
        {
            var res = new List<ProblemMark>();
            if (pts != null)
                foreach (var p in pts) res.Add(new ProblemMark(p, text));
            return res;
        }
    }

    // РЕЕСТР СЛОЁВ ПЛАГИНА.
    //
    // Имена слоёв — контракт между командами: LIRBUILD ищет стены по префиксу,
    // LIRLAYERS по нему же их не трогает, экспорт по нему же читает толщину.
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

        // Закреплённый объект: WALLS(H-200 FIX), COLUMNS(... FIX). Такую геометрию
        // построение НЕ двигает — вместо подтяжки объекта к сетке на него ставится
        // линия сетки (жёсткая цель). Метка суффиксом в имени слоя, а не XData:
        // слой виден в диспетчере, красится в свой цвет и выделяется рамкой, тогда
        // как невидимое состояние через месяц не объяснить («почему эта стена не
        // двигается, а соседняя двигается?»). Пробел перед FIX обязателен — иначе
        // под правило попал бы слой с именем вроде FIXTURES.
        private const string FixMarker = "FIX";

        // Дверные проёмы: WALL_DOORS(H-<высота>). Проверка намеренно широкая (без
        // "H-"): слой без высоты всё равно обязан считаться дверным и защищаться от
        // LIRLAYERS, а высота при разборе имени получает значение по
        // умолчанию.
        private const string DoorLayerPrefix = "WALL_DOORS(";

        // Пилоны-стержни: COLUMNS(SEC-RC_RECT B-.. H-..) и старый общий COLUMNS.
        private const string ColumnLayerName = "COLUMNS";

        // Линии готовой сетки.
        private const string TriangulationLayerName = "LINE_TRIANGULATION";

        // Метка XData на отрезках, нарисованных LIRBUILD. Нужна, чтобы повторный
        // запуск стирал ТОЛЬКО свою прошлую сетку: в LINE_TRIANGULATION лежит и
        // чужое — линии, разложенные LIRLAYERS, и контуры пилонов из
        // ExplodeColumnContours (их исходная полилиния удалена, восстановить
        // неоткуда). Имя приложения XData: до 31 знака, без пробелов.
        private const string MeshXDataApp = "MESHPLUGIN_GRID";

        // Контуры отверстий/проёмов в плите: внутри сетки нет.
        private const string HoleLayerName = "MESH_HOLES";

        // Контуры пилонов-пластин, сохранённые LIRPYLON. Сам пилон остаётся
        // осью-линией в WALLS(H-... PILON), а контур нужен LIRBUILD, чтобы
        // отпечатать его на сетке плиты (узлы в углах, мелкая сетка внутри).
        // В отличие от COLUMNS это НЕ пустота: внутри отпечатка сетка плиты есть.
        private const string PylonOutlineLayerName = "MESH_PYLONS";

        // Обозначение дверного проёма (квадрат в середине проёма) — только для
        // чертежа. Слой обязан быть отдельным от WALL_DOORS(H-...): экспорт читает
        // оттуда любые отрезки, и стороны квадрата стали бы фиктивными дверями.
        private const string DoorMarkLayerName = "WALL_DOORS_MARKS";
        private const double DoorMarkSize = 200.0;

        // Маркеры плагина: MESH_ANGLE_MARKS, MESH_GAP_MARKS.
        private const string MarkLayerPrefix = "MESH_";

        private const string AngleMarkLayerName = "MESH_ANGLE_MARKS";
        private const double AngleMarkRadius = 300.0;

        private const string GapMarkLayerName = "MESH_GAP_MARKS";
        private const double GapMarkRadius = 150.0; // Ø300 мм

        // Слой проблемных мест: места, из-за которых сетка не построилась или
        // построилась с дырами. Общий для LIRBUILD и LIREXPORT — показывает
        // проблемы последнего запуска.
        private const string ProblemLayerName = "ПРОБЛЕМА";
        private const double ProblemMarkRadius = 300.0;

        // Высота подписи внутри круга ПРОБЛЕМА. Круг Ø600 — короткое слово вроде
        // «вылез» помещается целиком, длинное («самопересечение») выходит за круг,
        // и это лучше нечитаемой мелочи: подпись нужна, чтобы инженер понял причину
        // без чтения консоли.
        private const double ProblemTextHeight = 150.0;

        // Памятка LIRHELP, вставленная в чертёж. Слой непечатаемый: текст нужен
        // инженеру на экране, а на лист попасть не должен. Высота 250 мм подобрана
        // под масштаб плана в миллиметрах.
        private const string HelpLayerName = "LIRHELP";
        private const short HelpLayerColor = 7;       // белый/чёрный по фону, не блёклый серый
        private const double HelpTextHeight = 250.0;  // обычный текст
        private const double HelpCommandHeight = 270.0;  // имена команд — крупнее
        // Стиль текста плагина: памятка LIRHELP и подписи к кругам ПРОБЛЕМА.
        private const string PluginTextStyleName = "ISOCPEUR";

        // Слой критических элементов, оставшийся от убранной команды MESHQUALITY:
        // на старых чертежах он ещё лежит, поэтому LIRBUILD его вычищает.
        private const string BadElementsLayerName = "ПЛОХИЕ";

        // Столько сегментов стен на плане не бывает: это предел здравого смысла,
        // а не допуск. Нужен против чертежей, где на слое стен лежит СЕТКА прошлого
        // построения (до 06.10.2026 LIRBUILD рисовал на текущий слой чертежа, и если
        // текущим оказывался слой стен, туда уходили все её отрезки). На таком входе
        // этапы, где каждая стена перебирается против каждого узла, считаются часами
        // — AutoCAD выглядит зависшим намертво. Лучше остановиться и сказать, почему.
        private const int WallSegmentsSanityLimit = 5000;

        // Стена (в том числе ось пилона).
        private static bool IsWallLayer(string layer)
        {
            return !string.IsNullOrEmpty(layer) && layer.StartsWith(WallLayerPrefix);
        }

        // Закреплённый объект: построение не двигает его геометрию.
        private static bool IsFixedLayer(string layer)
        {
            return !string.IsNullOrEmpty(layer)
                && layer.IndexOf(" " + FixMarker, StringComparison.Ordinal) >= 0;
        }

        // Имя слоя с меткой закрепления и без неё: " FIX" вставляется перед закрывающей
        // скобкой, чтобы имя осталось читаемым — WALLS(H-200) -> WALLS(H-200 FIX).
        private static string AddFixMarker(string layer)
        {
            if (IsFixedLayer(layer)) return layer;
            int close = layer.LastIndexOf(')');
            return close < 0 ? layer + " " + FixMarker
                             : layer.Substring(0, close) + " " + FixMarker + layer.Substring(close);
        }

        private static string RemoveFixMarker(string layer)
        {
            return IsFixedLayer(layer) ? layer.Replace(" " + FixMarker, "") : layer;
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
        // исходными контурами для новых построений (LIRWALLAXIS не принимает
        // их за контуры стен).
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
                || layer == HelpLayerName
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
