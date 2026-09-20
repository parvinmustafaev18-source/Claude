# Слои

Слой — единственный носитель смысла объекта: тип КЭ, толщина, роль. Геометрия
одинаковая, поведение задаёт имя слоя. Поэтому «сетка не видит X» почти всегда
означает «X лежит не в том слое».

## Таблица

| Слой | Кто создаёт | Кто читает | Что означает |
|---|---|---|---|
| `FOUNDATION_SLABS(H-<t>)` | LIRLAYERS | контур выбирается вручную | плита толщиной t мм |
| `LINE_TRIANGULATION` | LIRLAYERS, LIRBUILD, ExplodeColumnContours | экспорт | линии сетки |
| `WALLS(H-<t>)` | LIRWALLS, LIRWALLAXIS | `GetWallSegments`, `SnapWallsToGrid`, экспорт | ось стены, пластина толщиной t |
| `WALLS(H-<t> PILON)` | LIRPYLON | то же + `GetPylonCrossConstraints`, `GetPylonAxisTargets` | ось пилона; **не снапится** |
| `COLUMNS(SEC-RC_RECT B-<b> H-<h>)` | — (только старые чертежи) | `GetColumnPolygons`, `SnapColumnsToGrid` | сечение пилона (прежний режим) |
| `COLUMNS*` + DBPoint | — (только старые чертежи) | экспорт | центр пилона → стержень КЭ 10 |
| `WALL_DOORS(H-<h>)` | LIRDOORS | `GetDoorEndpoints`, `GetDoorJambConstraints`, `SnapDoorsToGrid`, экспорт | дверной проём высотой h |
| `WALL_DOORS_MARKS` | LIRDOORS | только чертёж | квадрат 200×200, в ЛИРУ не идёт |
| `MESH_HOLES` | LIRBUILD (`MovePolylinesToHoleLayer`) | `GetHolePolygons`, экспорт | отверстие/проём в плите |
| `MESH_PYLONS` | LIRPYLON (контур не стирает, а переносит) | `GetPylonOutlines` | контур пилона для отпечатка на сетке; **не пустота** — сетка внутри есть, мелкая |
| `MESH_ANGLE_MARKS` | `ValidateContour` | — | углы контура ≠ 90°, круги R300 |
| `MESH_GAP_MARKS` | `ValidateContour` | — | разрыв незамкнутого контура, круги R150 |
| `ПРОБЛЕМА` | LIRBUILD, LIREXPORT | — | места, где сетка не построилась, R300 |
| `ПЛОХИЕ` | — (наследие MESHQUALITY), LIRBUILD чистит | — | контуры элементов α < 0.3 на старых чертежах |

Константы имён: Commands.cs:1097–1109 (`ColumnLayerName`,
`TriangulationLayerName`, `HoleLayerName`, `DoorMarkLayerName`, `DoorMarkSize`),
Commands.cs:972–982 (маркерные слои и радиусы).

## Правила распознавания

**Все имена слоёв и все проверки живут в `Defs.cs`** — префиксы (`WallLayerPrefix`,
`DoorLayerPrefix`, `SlabLayerPrefix`, `PylonMarker`), имена (`HoleLayerName`,
`ProblemLayerName`, маркерные и мозаичные слои) и функции ниже. Проверять слой
литералом (`layer.StartsWith("WALLS(H-")`) в коде больше нельзя: пока литералы
были разбросаны, они успели разойтись — двери проверялись то по `WALL_DOORS(`,
то по `WALL_DOORS(H-`, и слой то защищался от LIRLAYERS, то нет.

- `IsWallLayer` / `IsPylonLayer` — стена и стена-ось пилона (суффикс `PILON`).
- `IsDoorLayer` — намеренно широкая проверка, без `H-`: слой без высоты всё равно
  дверной и должен защищаться, а высота получает значение по умолчанию.
- `IsColumnLayer` — `StartsWith("COLUMNS")`, старый общий слой `COLUMNS` тоже
  считается пилоном. `IsSlabLayer`, `IsMarkLayer` — по своим префиксам.
- `IsServiceLayer` — созданное самим плагином: плита, стены, двери,
  `WALL_DOORS_MARKS`, `LINE_TRIANGULATION`, `MESH_HOLES`, `MESH_PYLONS`,
  `COLUMNS*`.
  Используется в LIRWALLAXIS (что не принимать за
  контур стены), `MovePolylinesToHoleLayer` (что не превращать в отверстие).
- `KeepLayer` внутри LIRLAYERS — **шире** `IsServiceLayer`: плюс `IsMarkLayer`
  (`MESH_*`, `ПРОБЛЕМА`, `ПЛОХИЕ`). Списки разные намеренно: LIRLAYERS
  перекрашивает по рамке и обязана щадить даже маркеры.
- Толщина/высота из имени — `TryParseLayerHeight` (regex `H-([\d.,]+)`, запятая
  и точка равноправны, разбор инвариантный). Габариты пилона — `ColumnDimsRegex`.

## Форматирование имён

- Толщина/высота — `{value:0.###}`: `WALLS(H-200)`, `WALL_DOORS(H-2100)`.
- LIRWALLAXIS дополнительно округляет толщину до 10 мм —
  `WALLS(H-201)`/`WALLS(H-205)` появляться не должны.
- Габариты пилона — по bbox: `COLUMNS(SEC-RC_RECT B-600 H-300)`.

## Цвета

`EnsureLayer` (Commands.cs:1379) создаёт слой только если его нет — цвет
существующего слоя не меняется. `PickRandomColor` (Commands.cs:1344) берёт из
палитры `{1,2,3,4,5,6,30,50,90,140,200,220}` цвет, не занятый ни одним слоем
чертежа (`GetUsedLayerColors`, Commands.cs:1367); при исчерпании — первый
свободный ACI, кроме 7 (белый). Фиксированные цвета: двери 30 (оранжевый),
отверстия 6 (сиреневый), маркеры 1 (красный).

## Добавляешь новый слой

1. Константа и функция-проверка — в `Defs.cs`, рядом с остальными.
2. Внести в `IsServiceLayer` и/или `KeepLayer` — иначе LIRLAYERS перекрасит
   объекты, а LIRWALLAXIS примет их за контуры стен.
3. Дописать строку в таблицу выше.
