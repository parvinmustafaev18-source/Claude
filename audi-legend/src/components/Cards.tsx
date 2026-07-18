import React from "react";
import { AbsoluteFill, useCurrentFrame, interpolate, Easing } from "remotion";
import { titleFont, COLORS } from "../fonts";

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;

// Глава 4: horch → audi («слушай» по-латыни) + сноска про легенду.
export const WordCard: React.FC<{ durationInFrames: number }> = ({ durationInFrames }) => {
  const frame = useCurrentFrame();
  const horchIn = interpolate(frame, [10, 45], [0, 1], {
    ...clamp,
    easing: Easing.bezier(0.16, 1, 0.3, 1),
  });
  const arrowIn = interpolate(frame, [55, 85], [0, 1], clamp);
  const audiIn = interpolate(frame, [80, 115], [0, 1], {
    ...clamp,
    easing: Easing.bezier(0.16, 1, 0.3, 1),
  });
  const captionIn = interpolate(frame, [120, 150], [0, 1], clamp);
  const footnoteIn = interpolate(
    frame,
    [durationInFrames * 0.55, durationInFrames * 0.55 + 30],
    [0, 1],
    clamp,
  );

  return (
    <AbsoluteFill
      style={{
        backgroundColor: COLORS.bg,
        justifyContent: "center",
        alignItems: "center",
        fontFamily: titleFont,
      }}
    >
      <div style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 30 }}>
        <div
          style={{
            fontSize: 110,
            fontWeight: 300,
            color: COLORS.dim,
            fontFamily: "Georgia, serif",
            fontStyle: "italic",
            opacity: horchIn,
          }}
        >
          horch
        </div>
        <div style={{ fontSize: 60, color: COLORS.accent, opacity: arrowIn }}>↓</div>
        <div
          style={{
            fontSize: 150,
            fontWeight: 600,
            color: COLORS.text,
            letterSpacing: "0.08em",
            opacity: audiIn,
            scale: String(0.92 + 0.08 * audiIn),
          }}
        >
          audi
        </div>
        <div
          style={{
            fontSize: 44,
            fontWeight: 300,
            color: COLORS.silver,
            opacity: captionIn,
            marginTop: 10,
          }}
        >
          «слушай» — латынь
        </div>
        <div
          style={{
            fontSize: 30,
            fontWeight: 300,
            color: COLORS.dim,
            fontStyle: "italic",
            opacity: footnoteIn,
            marginTop: 26,
          }}
        >
          * точность легенды не подтверждена
        </div>
      </div>
    </AbsoluteFill>
  );
};

// Глава 5: схема США → $ → Германия; поток денег обрывается.
export const MoneyScheme: React.FC<{ durationInFrames: number }> = ({ durationInFrames }) => {
  const frame = useCurrentFrame();
  const breakAt = durationInFrames * 0.55;
  const broken = frame >= breakAt;
  const nodesIn = interpolate(frame, [5, 35], [0, 1], clamp);
  const lineIn = interpolate(frame, [30, 60], [0, 1], clamp);
  const gap = interpolate(frame, [breakAt, breakAt + 25], [0, 90], {
    ...clamp,
    easing: Easing.bezier(0.5, 0, 0.8, 1),
  });
  const crossIn = interpolate(frame, [breakAt + 10, breakAt + 35], [0, 1], clamp);

  const LINE_X0 = 560;
  const LINE_X1 = 1360;
  const MID = (LINE_X0 + LINE_X1) / 2;

  const Node: React.FC<{ x: number; label: string }> = ({ x, label }) => (
    <div
      style={{
        position: "absolute",
        left: x,
        top: 460,
        width: 320,
        height: 160,
        marginLeft: -160,
        border: `3px solid ${COLORS.silver}`,
        borderRadius: 16,
        display: "flex",
        justifyContent: "center",
        alignItems: "center",
        fontSize: 52,
        fontWeight: 500,
        color: COLORS.text,
        letterSpacing: "0.06em",
        opacity: nodesIn,
        backgroundColor: COLORS.card,
      }}
    >
      {label}
    </div>
  );

  return (
    <AbsoluteFill style={{ backgroundColor: COLORS.bg, fontFamily: titleFont }}>
      <Node x={400} label="США" />
      <Node x={1520} label="ГЕРМАНИЯ" />
      {/* линия кредитов, после краха расходится с разрывом */}
      <svg
        viewBox="0 0 1920 1080"
        style={{ position: "absolute", inset: 0, width: "100%", height: "100%" }}
      >
        <line
          x1={LINE_X0}
          y1={540}
          x2={LINE_X0 + (MID - gap / 2 - LINE_X0) * lineIn}
          y2={540}
          stroke={broken ? COLORS.accent : COLORS.silver}
          strokeWidth={6}
          strokeDasharray="24 16"
        />
        <line
          x1={MID + gap / 2 + (LINE_X1 - MID - gap / 2) * (1 - lineIn)}
          y1={540}
          x2={LINE_X1}
          y2={540}
          stroke={broken ? COLORS.accent : COLORS.silver}
          strokeWidth={6}
          strokeDasharray="24 16"
        />
      </svg>
      {/* доллары бегут по линии, пока поток не оборвался */}
      {[0, 1, 2].map((i) => {
        const progress = ((frame * 0.012 + i / 3) % 1);
        return (
          <div
            key={i}
            style={{
              position: "absolute",
              left: LINE_X0 + (LINE_X1 - LINE_X0) * progress,
              top: 470,
              fontSize: 54,
              fontWeight: 600,
              color: "#7bb26a",
              opacity: broken ? 0 : lineIn,
            }}
          >
            $
          </div>
        );
      })}
      {/* красный крест на месте разрыва */}
      <div
        style={{
          position: "absolute",
          left: MID,
          top: 540,
          translate: "-50% -50%",
          fontSize: 130,
          fontWeight: 600,
          color: COLORS.accent,
          opacity: crossIn,
          fontFamily: titleFont,
        }}
      >
        ✕
      </div>
      <div
        style={{
          position: "absolute",
          left: 0,
          right: 0,
          top: 700,
          textAlign: "center",
          fontSize: 40,
          fontWeight: 300,
          color: COLORS.dim,
          opacity: crossIn,
        }}
      >
        Октябрь 1929: американские кредиты обрываются
      </div>
    </AbsoluteFill>
  );
};

// Глава 5: четыре компании появляются по одной.
export const LogoList: React.FC = () => {
  const frame = useCurrentFrame();
  const NAMES = ["AUDI", "HORCH", "DKW", "WANDERER"];

  return (
    <AbsoluteFill
      style={{
        backgroundColor: COLORS.bg,
        justifyContent: "center",
        alignItems: "center",
        fontFamily: titleFont,
      }}
    >
      <div style={{ display: "flex", gap: 90, alignItems: "center" }}>
        {NAMES.map((n, i) => {
          const appear = interpolate(frame, [20 + i * 30, 45 + i * 30], [0, 1], {
            ...clamp,
            easing: Easing.bezier(0.16, 1, 0.3, 1),
          });
          return (
            <div
              key={n}
              style={{
                fontSize: 66,
                fontWeight: 500,
                letterSpacing: "0.1em",
                color: COLORS.text,
                opacity: appear,
                translate: `0px ${(1 - appear) * 40}px`,
                borderBottom: `4px solid ${COLORS.accent}`,
                paddingBottom: 14,
              }}
            >
              {n}
            </div>
          );
        })}
      </div>
    </AbsoluteFill>
  );
};

// Глава 7: слоган.
export const Vorsprung: React.FC = () => {
  const frame = useCurrentFrame();
  const inO = interpolate(frame, [10, 60], [0, 1], {
    ...clamp,
    easing: Easing.bezier(0.16, 1, 0.3, 1),
  });
  const spacing = interpolate(frame, [10, 90], [0.3, 0.14], {
    ...clamp,
    easing: Easing.bezier(0.16, 1, 0.3, 1),
  });
  const subIn = interpolate(frame, [70, 110], [0, 1], clamp);

  return (
    <AbsoluteFill
      style={{
        backgroundColor: "#000",
        justifyContent: "center",
        alignItems: "center",
        fontFamily: titleFont,
      }}
    >
      <div style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 40 }}>
        <div
          style={{
            fontSize: 88,
            fontWeight: 500,
            color: COLORS.text,
            letterSpacing: `${spacing}em`,
            opacity: inO,
            whiteSpace: "nowrap",
          }}
        >
          Vorsprung durch Technik
        </div>
        <div style={{ fontSize: 40, fontWeight: 300, color: COLORS.dim, opacity: subIn }}>
          «Превосходство высоких технологий»
        </div>
      </div>
    </AbsoluteFill>
  );
};

// Глава 9: три точки давления — США, Китай, Германия.
export const PressureMap: React.FC = () => {
  const frame = useCurrentFrame();
  const POINTS = [
    { x: 380, y: 470, label: "США", sub: "продажи буксуют", phase: 0 },
    { x: 960, y: 560, label: "ГЕРМАНИЯ", sub: "дорогое производство", phase: 30 },
    { x: 1540, y: 470, label: "КИТАЙ", sub: "местные конкуренты", phase: 60 },
  ];

  return (
    <AbsoluteFill
      style={{ backgroundColor: COLORS.bg, fontFamily: titleFont }}
    >
      {POINTS.map((p, i) => {
        const appear = interpolate(frame, [15 + i * 35, 45 + i * 35], [0, 1], clamp);
        const pulse = ((frame + p.phase) % 80) / 80;
        return (
          <div key={p.label} style={{ position: "absolute", left: p.x, top: p.y, opacity: appear }}>
            <div
              style={{
                position: "absolute",
                left: 0,
                top: 0,
                width: 30 + pulse * 130,
                height: 30 + pulse * 130,
                translate: "-50% -50%",
                borderRadius: "50%",
                border: `3px solid ${COLORS.accent}`,
                opacity: (1 - pulse) * 0.7,
              }}
            />
            <div
              style={{
                position: "absolute",
                left: 0,
                top: 0,
                width: 26,
                height: 26,
                translate: "-50% -50%",
                borderRadius: "50%",
                backgroundColor: COLORS.accent,
              }}
            />
            <div
              style={{
                position: "absolute",
                left: 0,
                top: 40,
                translate: "-50% 0",
                textAlign: "center",
                whiteSpace: "nowrap",
              }}
            >
              <div style={{ fontSize: 52, fontWeight: 500, color: COLORS.text, letterSpacing: "0.08em" }}>
                {p.label}
              </div>
              <div style={{ fontSize: 32, fontWeight: 300, color: COLORS.dim, marginTop: 6 }}>
                {p.sub}
              </div>
            </div>
          </div>
        );
      })}
    </AbsoluteFill>
  );
};
