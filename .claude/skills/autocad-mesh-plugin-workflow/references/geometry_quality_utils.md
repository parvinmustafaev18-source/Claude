# Готовые функции

Проверь здесь перед тем, как писать своё: пересечения, clipping, триангуляция,
point-in-polygon, α, снап, индексы узлов — всё уже есть. Все методы —
`private` в `partial class Commands`, поэтому вызываются из любого файла плагина
напрямую.

## Допуски — только из `MeshTol` (Defs.cs)

Число вроде `1e-3` или `100.0` писать в коде нельзя: одно и то же правило,
записанное в разных функциях по-разному, — источник самых дорогих багов (узел
раздваивается, ребро не находится, элемент теряется). Основные: `NodeMerge`
(точки ближе — один узел), `OnSegment` (точка на отрезке), `MinArea`
(вырожденный полигон), `MinPiece` (кусок отрезка после подрезки),
`DoorOnAxis` (дверь лежит на оси стены, 1 мм), `Collinear`, `Crossing`,
`MinElementSize`, `WallSnap`, `DoorSnap`, `MaxShift(step)`, `MinGridGap(step)`.

`NodeIndex` / `NodeIndex3` (SpatialGrid.cs) сливают точки **по допуску**
(поиск по соседним бакетам), а не по округлённым координатам: округление
разносило точки, отличающиеся на тысячную миллиметра, по разные стороны границы
клетки — сетка в этом месте не смыкалась. Ключ ребра — `EdgePairKey(ia,ib)` по
индексам узлов, не по координатам.

Полилинии читаются в WCS (`GetPolylineVertices` → `GetPoint3dAt`). Дуги и
полилинии вне плоскости XY проверяет `WarnBadPolylines`; снап такие полилинии
пропускает (`IsPolylineFlatXY`), потому что `SetPointAt` пишет в OCS.

## Geometry.cs — базовая геометрия

| Функция | Стр. | Что делает |
|---|---|---|
| `CrossProduct(a,b,c)` | 167 | векторное произведение, знак = сторона |
| `SegmentsIntersect(p1,p2,p3,p4)` | 172 | строгое пересечение (касание концом = false) |
| `LineIntersection(p1,p2,a,b)` | 290 | точка пересечения **прямых** |
| `IsPointOnSegment(p,a,b,eps)` | 132 | точка на отрезке с допуском |
| `IsPointInPolygon(p,poly)` | 149 | ray casting, граница не считается внутри |
| `IsPointInTriangle(p,a,b,c)` | 368 | по знакам трёх векторных произведений |
| `IsPolygonInsideContour(poly,contour)` | 187 | вершины + середины сторон внутри, сторон не пересекает; касание допустимо |
| `IsSegmentInsideContour(a,b,contour)` | 216 | то же для отрезка |
| `IsCellFullyInside(cell,contour)` | 236 | 4 угла внутри и сторон не пересекает |
| `PolygonArea(poly)` | 263 | со знаком (шнуровка) |
| `PolygonCentroid(poly)` | 277 | среднее вершин, для маркеров |
| `EnsureCcw(poly)` | 284 | разворот против часовой на месте |
| `CleanupPolygon(poly,eps)` | 341 | убрать совпадающие подряд и замыкающую вершину |
| `RemoveCollinearVertices(poly,eps=0.5)` | 27 | убрать вершины на прямой между соседями |
| `ComputeColumnCenters(polys)` | 12 | центроиды сечений пилонов |
| `ClipPolygonAgainstEdge(subj,a,b)` | 301 | одна полуплоскость Sutherland-Hodgman |
| `ClipPolygonToConvexCell(subj,cell)` | 330 | обрезка по выпуклой ячейке |
| `TriangulateSimplePolygon(poly, ref failed)` | 380 | ear-clipping |
| `TriangulateByDiagonalSplit(...)` | 447 | fallback: рекурсивное разрезание по диагонали |
| `IsConvexQuad(quad)` | 111 | одинаковый знак всех поворотов |
| `EdgeKey(a,b)` | 90 | ключ ребра, округление до 0.001, порядок неважен |
| `FindOppositeVertex(tri,a,b)` | 97 | третья вершина треугольника |

**Про триангуляцию.** Ear-clipping застревает на висячем узле, лежащем на прямом
участке границы: ухо в такой вершине вырождено, а выбросить её нельзя — узел
обязан остаться углом треугольников, иначе в ЛИРЕ он не связан с пластиной.
Тогда включается `TriangulateByDiagonalSplit` (диагональ не пересекает стороны,
её середина внутри, глубина ≤64). Полный провал считается в `failedPolygons` и
даёт круг `ПРОБЛЕМА`.

## Качество элементов α

Методика ЛИРА-САПР, мозаика «Качество пластин». Равносторонний треугольник и
квадрат дают 1.

| Функция / константа | Стр. | |
|---|---|---|
| `TriangleAlpha(a,b,c)` | Geometry.cs:54 | α = 4√3·S / (a²+b²+c²) |
| `QuadAlpha(q)` | Geometry.cs:67 | худшее из: отношение произведений α противолежащих пар и отклонение среднего от √3/2 |
| `QuadShapeOk(quad)` | Geometry.cs:84 | α ≥ `MinQualityAlpha` |
| `MinQualityAlpha` = 0.5 | Geometry.cs:51 | порог сращивания в MESHQUADMESH |
| `QualityAlphaMid` = 0.5, `QualityAlphaBad` = 0.3 | Quality.cs:21 | градации мозаики |
| `MinElementSize` = 100.0 | Geometry.cs:9 | минимальная длина ребра, мм |

## Quality.cs — оценка и разрезание пересечений

| Функция | Стр. | Что делает |
|---|---|---|
| `BuildQualityPlates(segments,centers,out failed)` | 139 | отрезки → пластины (та же логика, что в экспорте) |
| `DrawQualityMarks(...)` | 234 | заливки Solid по градациям либо контуры в `ПЛОХИЕ` |
| `SegmentCrossingPoint(...)` | 344 | Х-пересечение внутренностей; ближе 0.5 мм к концу = узловое касание, не в счёт |
| `SplitSegmentsAtIntersections(segs, out n)` | 367 | режет все Х-пересечения; пары ищет через `SpatialGrid` |

Порядок в `BuildQualityPlates` (dedup → пересечения → узлы → dedup) обязателен:
планарный граф строится по общим узлам, и пересечение без узла даёт грань,
накрывающую чужие линии.

Заливка вырожденного элемента (площадь < 1e-3) пропускается — Solid рисует
произвольное пятно. Вогнутый четырёхугольник делится на два треугольника по
внутренней диагонали, иначе Solid заливает с перехлёстом. Порядок вершин
четырёхугольника у `Solid` — «змейкой» (p0 p1 p3 p2), иначе бабочка.

## SpatialGrid.cs — индексы

- `SpatialGrid(cellSize)` (стр. 12): `Add(index, point)`, `QueryRadius(p, r)`.
  Без неё `WeldShortNodes`, `SplitSegmentsAtNodes`, `CloseOpenNodes`,
  `EnsureColumnCornerLinks` были бы O(n²) — на планах в тысячи сегментов это
  заметно.
- `NodeIndex` (стр. 68): `GetNode(p)` → индекс, совпадающие точки (округление до
  0.001) получают один индекс. Новый узел всегда получает `Nodes.Count-1`,
  поэтому параллельный список наращивается проверкой `idx == list.Count` сразу
  после `GetNode` — так сделано в `SmoothMesh`.

## Commands.cs — снап и сбор геометрии

| Функция / константа | Стр. | |
|---|---|---|
| `WallSnapTolerance` = 100.0 | 1393 | максимальный сдвиг стены/пилона к сетке |
| `DoorSnapTolerance` = 50.0 | 1619 | то же для двери (меньше — поедет проём) |
| `PylonNodeMinGap` = 100.0 | 1578 | минимальный просвет между узлами на оси пилона |
| `SnapCoord(v,origin,cell)` | 1395 | ближайшая линия сетки, если ближе допуска |
| `SnapToNearest(v,coords)` | 1686 | ближайшая координата из списка, ≤ `DoorSnapTolerance` |
| `SnapWallsToGrid` | 1404 | двигает объекты `WALLS(H-`, пропуская PILON |
| `SnapColumnsToGrid` | 1198 | жёсткий перенос пилона по левому нижнему углу bbox, вместе с точкой центра |
| `SnapDoorsToGrid` | 1627 | только координата вдоль стены |
| `GetWallSegments` / `GetDoorEndpoints` | 1503 / 1545 | сбор по слоям |
| `GetColumnPolygons` / `GetHolePolygons` | 1258 / 1281 | замкнутые контуры, уже `EnsureCcw` |
| `GetPylonCrossConstraints` | 1469 | перпендикуляр через середину оси PILON |
| `GetPylonAxisTargets` | 1586 | концы и середина оси как цели выравнивания |
| `GetDoorJambConstraints` / `AddJambConstraints` | 1706 / 1770 | поперечные разрезы через косяки + мягкие цели |
| `GetPolylineVertices(pl)` | 1797 | вершины полилинии |
| `ValidateContour(...)` | 862 | замкнутость, дуги, самопересечения, углы ≠ 90° |
| `EnsureLayer` / `PickRandomColor` / `GetUsedLayerColors` | 1379 / 1344 / 1367 | слои и цвета |
| `DrawMarkCircles` / `EraseMarksOnLayer` / `MarkProblemPoints` | 1083 / 997 / 987 | маркеры |

`ValidateContour` отвергает дуги: ЛИРА-САПР их не принимает, дуги заменяются
хордами. Углы ≠ 90° — не отказ, а предупреждение с координатами и кругами в
`MESH_ANGLE_MARKS`; вершины с углом > 175° считаются промежуточными точками на
прямой стороне, а не углами.

## Обработка отрезков сетки (QuadMesh.cs)

| Функция | Стр. |
|---|---|
| `DeduplicateSegments` | 1607 |
| `SegmentLiesOnContour` / `RemoveSegmentsOnContour` | 1621 / 1637 |
| `ResolveOverlappingSegments` (+ `CollinearGroup`, `AddSegmentToLineGroups`) | 1700 (1659, 1663) |
| `SplitSegmentsAtNodes` / `SplitSegmentsAtPoints` | 1253 / 876 |
| `ClipSegmentsToContour` / `ClipSegmentsOutsideColumns` | 1474 / 1537 |
| `CellTouchesWalls` / `SegmentTouchesPolygon` / `SplitPolygonByWalls` | 1757 / 1774 / 1791 |
| `CellInsideAnyColumn` / `CellCenterInsideAnyColumn` / `PieceInsideAnyColumn` / `PointInsideAnyVoid` | 1388 / 1421 / 1402 / 1437 |
| `AddQuadSegments` / `AddTriSegments` / `DrawSegment` | 1453 / 1461 / 1446 |
| `BuildGridCoords` (две перегрузки) | 903, 915 |
| `WeldShortNodes` / `EnsureColumnCornerLinks` / `CloseOpenNodes` / `SmoothMesh` | 774 / 1321 / 997 / 658 |

`ResolveOverlappingSegments` — финальная зачистка: коллинеарные отрезки на одной
прямой разбиваются концами друг друга на элементарные интервалы, каждый
выводится один раз, накрытые стеной выбрасываются (стена уже нарисована
пользователем). ЛИРА-САПР требует, чтобы отрезки на плане не накладывались.
