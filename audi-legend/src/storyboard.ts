// Раскадровка «Собрано в легенду» — Выпуск №1: история Audi.
// Тайминги — оценка при темпе ~140 слов/мин; после записи озвучки
// скорректируй startSec/endSec глав и weight кадров по факту.

export const FPS = 30;

export type Grade = "color" | "sepia" | "bw" | "muted";

export type ShotKind =
  | "photo" // фото из public/photos (или плейсхолдер, если image: null)
  | "black" // чёрный экран / пауза
  | "montage" // быстрая нарезка нескольких фото
  | "rings-intro" // обложка выпуска: четыре кольца + название
  | "word-card" // карточка horch → audi
  | "money-scheme" // схема США → $ → Германия
  | "logo-list" // четыре логотипа появляются по одному
  | "rings-merge" // логотипы сливаются в четыре кольца
  | "rings-reveal" // логотип Audi проявляется из темноты
  | "vorsprung" // шрифтовой кадр со слоганом
  | "pressure-map" // три точки давления: США, Китай, Германия
  | "rings-final"; // финал: кольца + призыв подписаться

export type Shot = {
  id: string; // имя файла: public/photos/<id>.jpg
  kind: ShotKind;
  anchor: string; // первые слова реплики (якорь из раскадровки)
  visual: string; // что должно быть на экране
  title?: string; // текстовый оверлей (титр)
  image?: string | null; // имя файла в public/photos; null = плейсхолдер
  montage?: { id: string; image: string | null }[]; // для kind: "montage"
  weight?: number; // относительная длительность внутри главы
  grade?: Grade; // переопределение цветообработки главы
  sfx?: string; // заметка по звуку
};

export type Chapter = {
  n: number;
  name: string;
  startSec: number;
  endSec: number;
  grade: Grade;
  shots: Shot[];
};

// Аудио: положи файлы в public/ и укажи имена вместо null.
export const audioConfig = {
  voiceover: null as string | null, // напр. "voiceover.mp3"
  music: null as string | null, // напр. "music.mp3"
  musicVolume: 0.18,
};

export const chapters: Chapter[] = [
  {
    n: 1,
    name: "Интро",
    startSec: 0,
    endSec: 90,
    grade: "color",
    shots: [
      {
        id: "ch1-rings",
        kind: "rings-intro",
        anchor: "Всем привет, дорогие слушатели…",
        visual: "Обложка выпуска: логотип подкаста → четыре кольца Audi крупно",
        weight: 2,
      },
      {
        id: "ch1-horch",
        kind: "photo",
        anchor: "На примере Audi — бренда…",
        visual:
          "Медленный наезд на современный логотип Audi, затем старое фото Августа Хорьха",
        image: null,
        weight: 2,
      },
      {
        id: "ch1-pause",
        kind: "black",
        anchor: "[Пауза]",
        visual: "Чёрный экран",
        weight: 0.5,
      },
      {
        id: "ch1-teaser",
        kind: "montage",
        anchor: "Это путь компании…",
        visual:
          "Монтаж-тизер: старинный автомобиль → гонки 30-х → quattro на снегу → современный Audi",
        montage: [
          { id: "ch1-teaser-1", image: null },
          { id: "ch1-teaser-2", image: null },
          { id: "ch1-teaser-3", image: null },
          { id: "ch1-teaser-4", image: null },
        ],
        weight: 2,
      },
    ],
  },
  {
    n: 2,
    name: "Становление",
    startSec: 90,
    endSec: 240,
    grade: "sepia",
    shots: [
      {
        id: "ch2-horch-portrait",
        kind: "photo",
        anchor: "Одним из первых главных героев…",
        visual: "Портрет молодого Августа Хорьха",
        title: "Август Хорьх, 1868",
        image: null,
      },
      {
        id: "ch2-factory",
        kind: "photo",
        anchor: "Позже Хорьх устроился на вагоностроительный…",
        visual: "Архивное фото завода/цеха конца XIX века",
        image: null,
      },
      {
        id: "ch2-benz-velo",
        kind: "photo",
        anchor: "Но в двадцать лет всё изменилось…",
        visual: "Фото Саксонии/здания училища, затем Benz Velo",
        title: "Benz Velo, 1896",
        image: null,
      },
      {
        id: "ch2-benz",
        kind: "photo",
        anchor: "Хорьх написал письмо самому Карлу Бенцу…",
        visual: "Портрет Карла Бенца",
        title: "Карл Бенц",
        image: null,
      },
      {
        id: "ch2-mannheim",
        kind: "photo",
        anchor: "Работа у Бенца стала…",
        visual: "Фото раннего завода Benz в Мангейме",
        title: "Мангейм",
        image: null,
      },
    ],
  },
  {
    n: 3,
    name: "Horch: первая фирма",
    startSec: 240,
    endSec: 360,
    grade: "sepia",
    shots: [
      {
        id: "ch3-horch-cie",
        kind: "photo",
        anchor: "В 1899 году Хорьх уходит…",
        visual: "Логотип A. Horch & Cie / фото первой мастерской",
        title: "1899. A. Horch & Cie",
        image: null,
      },
      {
        id: "ch3-first-car",
        kind: "photo",
        anchor: "Сначала это крошечная мастерская…",
        visual: "Фото первого автомобиля Horch",
        title: "Первый Horch, 1900–1901",
        image: null,
      },
      {
        id: "ch3-workers",
        kind: "photo",
        anchor: "Хорьх строил не только машины…",
        visual: "Фото рабочих завода, цех",
        image: null,
      },
      {
        id: "ch3-six-cyl",
        kind: "photo",
        anchor: "К 1907 году компания представляет…",
        visual: "Фото шестицилиндрового Horch",
        title: "1907. Первый 6-цилиндровый",
        image: null,
      },
    ],
  },
  {
    n: 4,
    name: "Конфликт и рождение AUDI",
    startSec: 360,
    endSec: 570,
    grade: "sepia",
    shots: [
      {
        id: "ch4-races",
        kind: "photo",
        anchor: "В начале двадцатого века гонки…",
        visual: "Архивные фото ранних гонок (Horch на гонках)",
        image: null,
        weight: 1.5,
      },
      {
        id: "ch4-exile",
        kind: "photo",
        anchor: "В итоге в 1909 году основателю пришлось…",
        visual: "Портрет Хорьха, обработка мрачнее",
        title: "1909. Изгнание",
        image: null,
      },
      {
        id: "ch4-court",
        kind: "photo",
        anchor: "Бывшие партнёры подали в суд. И выиграли.",
        visual: "Затемнение, документ/газета эпохи (стилизация)",
        title: "Фамилия Horch — больше не его",
        image: null,
      },
      {
        id: "ch4-pen",
        kind: "photo",
        anchor: "[Звуковой эффект — скрип пера]",
        visual: "Крупно: перо, чернила, бумага",
        image: null,
        weight: 0.8,
        sfx: "СФХ: скрип пера",
      },
      {
        id: "ch4-latin",
        kind: "word-card",
        anchor: "Пришлось искать новое название…",
        visual: "Комната, мальчик с учебником латыни → horch → audi",
        weight: 1.5,
      },
      {
        id: "ch4-audi-founded",
        kind: "photo",
        anchor: "Новая фирма получила имя Audi…",
        visual: "Рекламный плакат Audi с ухом или ранний логотип Audi",
        title: "1910. Audi Automobilwerke",
        image: null,
        weight: 1.2,
      },
    ],
  },
  {
    n: 5,
    name: "Четыре кольца и Auto Union",
    startSec: 570,
    endSec: 780,
    grade: "bw",
    shots: [
      {
        id: "ch5-early-audi",
        kind: "photo",
        anchor: "Август Хорьх создал не просто…",
        visual: "Фото ранних Audi (Type A/B)",
        image: null,
      },
      {
        id: "ch5-ww1",
        kind: "photo",
        anchor: "Началась Первая мировая война",
        visual: "Архивные кадры ПМВ (общие планы, без жести)",
        title: "1914",
        image: null,
      },
      {
        id: "ch5-globe",
        kind: "photo",
        anchor: "После войны Audi выпускала…",
        visual: "Логотип «единица на фоне земного шара»",
        title: "1921",
        image: null,
      },
      {
        id: "ch5-crash",
        kind: "photo",
        anchor: "24 октября 1929 года. Нью-Йоркская биржа",
        visual: "Фото толпы у биржи, Чёрный четверг",
        title: "24.10.1929. Чёрный четверг",
        image: null,
        weight: 1.2,
      },
      {
        id: "ch5-scheme",
        kind: "money-scheme",
        anchor: "А причём здесь немецкий автопром?",
        visual: "Схема: США → $ → Германия, стрелка обрывается",
        weight: 1.2,
      },
      {
        id: "ch5-logos",
        kind: "logo-list",
        anchor: "Четыре немецкие компании…",
        visual: "Audi, Horch, DKW, Wanderer — появляются по одному",
      },
      {
        id: "ch5-merge",
        kind: "rings-merge",
        anchor: "В 1932 году родился концерн Auto Union",
        visual: "Логотипы сливаются в четыре кольца",
        title: "1932. Auto Union",
        weight: 1.2,
      },
    ],
  },
  {
    n: 6,
    name: "Взлёт и крах Auto Union",
    startSec: 780,
    endSec: 990,
    grade: "bw",
    shots: [
      {
        id: "ch6-horch-old",
        kind: "photo",
        anchor: "23 августа 1932 года Август Хорьх…",
        visual: "Фото пожилого Хорьха",
        title: "Спустя 23 года",
        image: null,
      },
      {
        id: "ch6-gift",
        kind: "photo",
        anchor: "В качестве подарка ему вручили автомобиль — Horch",
        visual: "Фото представительского Horch 30-х",
        image: null,
      },
      {
        id: "ch6-arrows",
        kind: "photo",
        anchor: "Уже с 1934 года гоночные болиды…",
        visual: "Фото Серебряных стрел Auto Union (Type C и др.)",
        title: "«Серебряные стрелы»",
        image: null,
        weight: 1.2,
      },
      {
        id: "ch6-stuck",
        kind: "photo",
        anchor: "В 1935 году рекордный Auto Union…",
        visual: "Фото рекордного заезда Ханса Штука",
        title: "Ханс Штук. 327 км/ч",
        image: null,
      },
      {
        id: "ch6-cut",
        kind: "black",
        anchor: "[Музыка усиливается и резко обрывается]",
        visual: "Резкий переход в чёрный",
        weight: 0.4,
      },
      {
        id: "ch6-ww2",
        kind: "photo",
        anchor: "Осенью 1939 года началась Вторая мировая",
        visual: "Общий архивный кадр (сдержанно)",
        title: "1939",
        image: null,
      },
      {
        id: "ch6-ruins",
        kind: "photo",
        anchor: "Концерн Auto Union прекратил существование",
        visual: "Фото разрушенных/пустых заводов, ГДР",
        title: "Бренд Audi — исчез",
        image: null,
      },
      {
        id: "ch6-forever",
        kind: "black",
        anchor: "Казалось бы, навсегда",
        visual: "Долгий чёрный экран",
        weight: 0.8,
      },
    ],
  },
  {
    n: 7,
    name: "Возрождение",
    startSec: 990,
    endSec: 1290,
    grade: "bw",
    shots: [
      {
        id: "ch7-ingolstadt",
        kind: "photo",
        anchor: "В конце 1945 года в Ингольштадте…",
        visual: "Фото послевоенного Ингольштадта / складов",
        title: "1945. Ингольштадт",
        image: null,
      },
      {
        id: "ch7-dkw",
        kind: "photo",
        anchor: "Поэтому первыми машинами становятся…",
        visual: "Фото DKW послевоенных лет",
        title: "DKW. Не громко, но верно",
        image: null,
      },
      {
        id: "ch7-flick",
        kind: "photo",
        anchor: "В 1954 году в игру входит Фридрих Флик",
        visual: "Портрет Флика",
        title: "Фридрих Флик",
        image: null,
      },
      {
        id: "ch7-daimler",
        kind: "photo",
        anchor: "в 1958 году контрольный пакет уходит Daimler-Benz",
        visual: "Логотипы: Auto Union → под крыло Mercedes (схема)",
        title: "1958",
        image: null,
      },
      {
        id: "ch7-kraus",
        kind: "photo",
        anchor: "Daimler-Benz присылает… Людвига Крауса",
        visual: "Фото Людвига Крауса (иначе — завод Ингольштадта)",
        title: "Людвиг Краус",
        image: null,
      },
      {
        id: "ch7-vw",
        kind: "photo",
        anchor: "Volkswagen поглощает Auto Union GmbH",
        visual: "Логотип VW + фото «Жука»",
        title: "1965",
        image: null,
      },
      {
        id: "ch7-return",
        kind: "rings-reveal",
        anchor: "Из архива достают то, что молчало двадцать лет",
        visual: "Медленное появление логотипа Audi из темноты",
        title: "Audi вернулась",
      },
      {
        id: "ch7-secret",
        kind: "photo",
        anchor: "Тайно, без санкции руководства…",
        visual: "Фото Audi 100 под тканью / презентация",
        image: null,
      },
      {
        id: "ch7-audi100",
        kind: "photo",
        anchor: "Машину назвали Audi 100",
        visual: "Фото Audi 100 (1968) крупно",
        title: "Audi 100. 1968",
        image: null,
        grade: "color",
        weight: 1.2,
      },
      {
        id: "ch7-vorsprung",
        kind: "vorsprung",
        anchor: "Vorsprung durch Technik",
        visual: "Шрифтовой кадр со слоганом",
        grade: "color",
      },
    ],
  },
  {
    n: 8,
    name: "Пиех и quattro",
    startSec: 1290,
    endSec: 1620,
    grade: "muted",
    shots: [
      {
        id: "ch8-piech",
        kind: "photo",
        anchor: "1972 год. В Ингольштадт приходит…",
        visual: "Фото Фердинанда Пиеха",
        title: "Фердинанд Пиех",
        image: null,
      },
      {
        id: "ch8-iltis",
        kind: "photo",
        anchor: "Инженеры Audi испытывают… Iltis",
        visual: "Фото VW Iltis на снегу",
        image: null,
      },
      {
        id: "ch8-piech2",
        kind: "photo",
        anchor: "Пиех думал иначе",
        visual: "Портрет Пиеха, крупнее",
        image: null,
        weight: 0.8,
      },
      {
        id: "ch8-geneva",
        kind: "photo",
        anchor: "Март 1980 года. Женевский автосалон",
        visual: "Фото Audi quattro на стенде Женевы-1980",
        title: "1980. quattro",
        image: null,
      },
      {
        id: "ch8-motion",
        kind: "photo",
        anchor: "[СФХ: рёв двигателя → тишина]",
        visual: "Кадр quattro в движении, стоп-кадр",
        image: null,
        sfx: "СФХ: рёв двигателя → тишина",
      },
      {
        id: "ch8-rally",
        kind: "photo",
        anchor: "Уже в 1981 году quattro выходит на ралли",
        visual: "Фото ралли группы B: quattro в грязи/снегу",
        title: "WRC. Разгром",
        image: null,
        weight: 1.2,
      },
      {
        id: "ch8-lemans",
        kind: "photo",
        anchor: "по-настоящему легендарной… в 1999 году. Ле-Ман",
        visual: "Фото ночного Ле-Мана",
        title: "Ле-Ман. 24 часа",
        image: null,
      },
      {
        id: "ch8-rcars",
        kind: "montage",
        anchor: "С 2000 по 2014 год — тринадцать побед",
        visual: "Серия фото: R8 → R10 → R18",
        title: "13 побед за 15 лет",
        montage: [
          { id: "ch8-r8", image: null },
          { id: "ch8-r10", image: null },
          { id: "ch8-r18", image: null },
        ],
        weight: 1.2,
      },
      {
        id: "ch8-dieselgate",
        kind: "photo",
        anchor: "Правда у этого триумфа была и обратная сторона",
        visual: "Затемнение; заголовки газет 2015 (стилизованно)",
        title: "2015. Дизельгейт",
        image: null,
        grade: "bw",
      },
    ],
  },
  {
    n: 9,
    name: "Что дальше?",
    startSec: 1620,
    endSec: 1800,
    grade: "color",
    shots: [
      {
        id: "ch9-lineup",
        kind: "photo",
        anchor: "Итак, где Audi находится сегодня?",
        visual: "Современный модельный ряд Audi",
        image: null,
      },
      {
        id: "ch9-map",
        kind: "pressure-map",
        anchor: "В США продажи буксуют… В Китае…",
        visual: "Карта/схема: США, Китай, Германия — три точки давления",
      },
      {
        id: "ch9-etron",
        kind: "photo",
        anchor: "Сегодня Audi снова стоит перед выбором",
        visual: "Современный электрический Audi (e-tron) в движении",
        image: null,
      },
      {
        id: "ch9-final",
        kind: "rings-final",
        anchor: "списывать эту марку со счетов пока рановато",
        visual: "Четыре кольца, медленное затемнение, призыв подписаться",
        weight: 1.3,
      },
    ],
  },
];

// ---- Тайминг ----

export const DIVIDER_SEC = 2; // чёрный разделитель перед главами 2–9

export type TimedShot = {
  shot: Shot;
  chapter: Chapter;
  indexInChapter: number;
  globalIndex: number;
  from: number; // кадры
  duration: number; // кадры
};

export type TimedDivider = {
  chapter: Chapter;
  from: number;
  duration: number;
};

export const buildTimeline = () => {
  const shots: TimedShot[] = [];
  const dividers: TimedDivider[] = [];
  let globalIndex = 0;

  for (const chapter of chapters) {
    const dividerSec = chapter.n === 1 ? 0 : DIVIDER_SEC;
    const windowSec = chapter.endSec - chapter.startSec - dividerSec;
    const totalWeight = chapter.shots.reduce((s, x) => s + (x.weight ?? 1), 0);

    if (dividerSec > 0) {
      dividers.push({
        chapter,
        from: Math.round(chapter.startSec * FPS),
        duration: Math.round(dividerSec * FPS),
      });
    }

    let cursor = chapter.startSec + dividerSec;
    for (let i = 0; i < chapter.shots.length; i++) {
      const shot = chapter.shots[i];
      const sec = (windowSec * (shot.weight ?? 1)) / totalWeight;
      const from = Math.round(cursor * FPS);
      const end =
        i === chapter.shots.length - 1
          ? Math.round(chapter.endSec * FPS)
          : Math.round((cursor + sec) * FPS);
      shots.push({
        shot,
        chapter,
        indexInChapter: i,
        globalIndex: globalIndex++,
        from,
        duration: end - from,
      });
      cursor += sec;
    }
  }

  return { shots, dividers };
};

export const TOTAL_FRAMES = chapters[chapters.length - 1].endSec * FPS;
