# Слои

Слой — единственный носитель смысла объекта: тип КЭ, толщина, роль. Геометрия
одинаковая, поведение задаёт имя слоя. Поэтому «сетка не видит X» почти всегда
означает «X лежит не в том слое».

## Таблица

| Слой | Кто создаёт | Кто читает | Что означает |
|---|---|---|---|
| `FOUNDATION_SLABS(H-<t>)` | MESHLAYERS | контур выбирается вручную | плита толщиной t мм |
| `LINE_TRIANGULATION` | MESHLAYERS, MESHQUADMESH, ExplodeColumnContours | MESHQUALITY, экспорт | линии сетки |
| `WALLS(H-<t>)` | MESHWALLS, MESHWALLAXIS | `GetWallSegments`, `SnapWallsToGrid`, MESHQUALITY, экспорт | ось стены, пластина толщиной t |
| `WALLS(H-<t> PILON)` | MESHCOLUMNCROSS | то же + `GetPylonCrossConstraints`, `GetPylonAxisTargets` | ось пилона; **не снапится** |
| `COLUMNS(SEC-RC_RECT B-<b> H-<h>)` | MESHCOLUMNSBAR | `GetColumnPolygons`, `SnapColumnsToGrid` | сечение пилона (старый режим) |
| `COLUMNS*` + DBPoint | MESHCOLUMNSBAR | экспорт, MESHQUALITY | центр пилона → стержень КЭ 10 |
| `WALL_DOORS(H-<h>)` | MESHDOORS | `GetDoorEndpoints`, `GetDoorJambConstraints`, `SnapDoorsToGrid`, экспорт | дверной проём высотой h |
| `WALL_DOORS_MARKS` | MESHDOORS | только чертёж | квадрат 200×200, в ЛИРУ не идёт |
| `MESH_HOLES` | MESHQUADMESH (`MovePolylinesToHoleLayer`) | `GetHolePolygons`, экспорт | отверстие/проём в плите |
| `MESH_PYLONS` | MESHCOLUMNCROSS (контур не стирает, а переносит) | `GetPylonOutlines` | контур пилона для отпечатка на сетке; **не пустота** — сетка внутри есть, мелкая |
| `MESH_ANGLE_MARKS` | `ValidateContour` | — | углы контура ≠ 90°, круги R300 |
| `MESH_GAP_MARKS` | `ValidateContour` | — | разрыв незамкнутого контура, круги R150 |
| `ПРОБЛЕМА` | MESHQUADMESH, MESHEXPORTTXT | — | места, где сетка не построилась, R300 |
| `MESH_QUALITY_GOOD/MID/BAD` | MESHQUALITY | — | мозаика α (зелёный/жёлтый/красный) |
| `ПЛОХИЕ` | MESHQUALITY, MESHQUADMESH (чистит) | — | контуры элементов α < 0.3 |

Константы имён: Commands.cs:1097–1109 (`ColumnLayerName`,
`TriangulationLayerName`, `HoleLayerName`, `DoorMarkLayerName`, `DoorMarkSize`),
Commands.cs:972–982 (маркерные слои и радиусы), Quality.cs:15–26.

## Правила распознавания

**Все имена слоёв и все проверки живут в `Defs.cs`** — префиксы (`WallLayerPrefix`,
`DoorLayerPrefix`, `SlabLayerPrefix`, `PylonMarker`), имена (`HoleLayerName`,
`ProblemLayerName`, маркерные и мозаичные слои) и функции ниже. Проверять слой
литералом (`layer.StartsWith("WALLS(H-")`) в коде больше нельзя: пока литералы
были разбросаны, они успели разойтись — двери проверялись то по `WALL_DOORS(`,
то по `WALL_DOORS(H-`, и слой то защищался от MESHLAYERS, то нет.

- `IsWallLayer` / `IsPylonLayer` — стена и стена-ось пилона (суффикс `PILON`).
- `IsDoorLayer` — намеренно широкая проверка, без `H-`: слой без высоты всё равно
  дверной и должен защищаться, а высота получает значение по умолчанию.
- `IsColumnLayer` — `StartsWith("COLUMNS")`, старый общий слой `COLUMNS` тоже
  считается пилоном. `IsSlabLayer`, `IsMarkLayer` — по своим префиксам.
- `IsServiceLayer` — созданное самим плагином: плита, стены, двери,
  `WALL_DOORS_MARKS`, `LINE_TRIANGULATION`, `MESH_HOLES`, `MESH_PYLONS`,
  `COLUMNS*`.
  Используется в MESHCLEAN (что не удалять), MESHWALLAXIS (что не принимать за
  контур стены), `MovePolylinesToHoleLayer` (что не превращать в отверстие).
- `KeepLayer` внутри MESHLAYERS — **шире** `IsServiceLayer`: плюс `IsMarkLayer`
  (`MESH_*`, `ПРОБЛЕМА`, `ПЛОХИЕ`). Списки разные намеренно: MESHLAYERS
  перекрашивает по рамке и обязана щадить даже маркеры.
- Толщина/высота из имени — `TryParseLayerHeight` (regex `H-([\d.,]+)`, запятая
  и точка равноправны, разбор инвариантный). Габариты пилона — `ColumnDimsRegex`.

## Форматирование имён

- Толщина/высота — `{value:0.###}`: `WALLS(H-200)`, `WALL_DOORS(H-2100)`.
- MESHWALLAXIS дополнительно округляет толщину до 10 мм —
  `WALLS(H-201)`/`WALLS(H-205)` появляться не должны.
- Габариты пилона — по bbox: `COLUMNS(SEC-RC_RECT B-600 H-300)`.

## Цвета

`EnsureLayer` (Commands.cs:1379) создаёт слой только если его нет — цвет
существующего слоя не меняется. `PickRandomColor` (Commands.cs:1344) берёт из
палитры `{1,2,3,4,5,6,30,50,90,140,200,220}` цвет, не занятый ни одним слоем
чертежа (`GetUsedLayerColors`, Commands.cs:1367); при исчерпании — первый
свободный ACI, кроме 7 (белый). Фиксированные цвета: двери 30 (оранжевый),
отверстия 6 (сиреневый), маркеры 1 (красный), `ПЛОХИЕ` 7 (белый), мозаика
3/2/1.

## Добавляешь новый слой

1. Константа и функция-проверка — в `Defs.cs`, рядом с остальными.
2. Внести в `IsServiceLayer` и/или `KeepLayer` — иначе MESHCLEAN его сотрёт, а
   MESHLAYERS перекрасит объекты.
3. Дописать строку в таблицу выше.
