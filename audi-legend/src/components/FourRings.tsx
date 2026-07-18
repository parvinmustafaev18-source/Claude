import React from "react";
import { AbsoluteFill, useCurrentFrame, interpolate, Easing } from "remotion";
import { titleFont, COLORS } from "../fonts";

const R = 110;
const STROKE = 18;
const CIRC = 2 * Math.PI * R;
const CY = 200;
const FINAL_CX = [445, 615, 785, 955]; // перекрытие как у настоящих колец

const Rings: React.FC<{
  cx?: number[];
  drawProgress?: number; // 0..1 — прорисовка окружностей
  color?: string;
  glow?: number; // 0..1
}> = ({ cx = FINAL_CX, drawProgress = 1, color = COLORS.silver, glow = 0 }) => {
  return (
    <svg
      viewBox="0 0 1400 400"
      style={{
        width: 1100,
        filter: glow > 0 ? `drop-shadow(0 0 ${30 * glow}px rgba(217,217,222,${0.5 * glow}))` : undefined,
      }}
    >
      {cx.map((x, i) => (
        <circle
          key={i}
          cx={x}
          cy={CY}
          r={R}
          fill="none"
          stroke={color}
          strokeWidth={STROKE}
          strokeDasharray={CIRC}
          strokeDashoffset={CIRC * (1 - Math.min(1, Math.max(0, drawProgress * 4 - i * 0.6)))}
          transform={`rotate(-90 ${x} ${CY})`}
        />
      ))}
    </svg>
  );
};

// Глава 1: обложка выпуска — кольца прорисовываются, появляется название подкаста.
export const RingsIntro: React.FC = () => {
  const frame = useCurrentFrame();
  const draw = interpolate(frame, [10, 110], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
    easing: Easing.bezier(0.4, 0, 0.2, 1),
  });
  const titleIn = interpolate(frame, [80, 130], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
    easing: Easing.bezier(0.16, 1, 0.3, 1),
  });
  const subIn = interpolate(frame, [120, 165], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });

  return (
    <AbsoluteFill
      style={{
        backgroundColor: COLORS.bg,
        justifyContent: "center",
        alignItems: "center",
        fontFamily: titleFont,
      }}
    >
      <div style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 40 }}>
        <Rings drawProgress={draw} />
        <div
          style={{
            fontSize: 96,
            fontWeight: 600,
            color: COLORS.text,
            letterSpacing: "0.06em",
            opacity: titleIn,
            translate: `0px ${(1 - titleIn) * 30}px`,
          }}
        >
          СОБРАНО В ЛЕГЕНДУ
        </div>
        <div
          style={{
            fontSize: 40,
            fontWeight: 300,
            color: COLORS.dim,
            letterSpacing: "0.3em",
            opacity: subIn,
            textTransform: "uppercase",
          }}
        >
          Выпуск №1 · История Audi
        </div>
      </div>
    </AbsoluteFill>
  );
};

// Глава 5: четыре логотипа сливаются в четыре кольца.
export const RingsMerge: React.FC = () => {
  const frame = useCurrentFrame();
  const t = interpolate(frame, [25, 110], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
    easing: Easing.bezier(0.65, 0, 0.35, 1),
  });
  const START_CX = [150, 520, 880, 1250];
  const cx = FINAL_CX.map((f, i) => START_CX[i] + (f - START_CX[i]) * t);
  const namesOpacity = interpolate(frame, [15, 55], [1, 0], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const NAMES = ["AUDI", "DKW", "HORCH", "WANDERER"];

  return (
    <AbsoluteFill
      style={{
        backgroundColor: COLORS.bg,
        justifyContent: "center",
        alignItems: "center",
        fontFamily: titleFont,
      }}
    >
      <div style={{ position: "relative", width: 1100 }}>
        <Rings cx={cx} glow={t} />
        <div
          style={{
            position: "absolute",
            top: "78%",
            left: 0,
            width: "100%",
            display: "flex",
            justifyContent: "space-between",
            padding: "0 30px",
            opacity: namesOpacity,
          }}
        >
          {NAMES.map((n) => (
            <div
              key={n}
              style={{
                fontSize: 36,
                fontWeight: 400,
                letterSpacing: "0.15em",
                color: COLORS.dim,
              }}
            >
              {n}
            </div>
          ))}
        </div>
      </div>
    </AbsoluteFill>
  );
};

// Глава 7: логотип Audi медленно проявляется из темноты.
export const RingsReveal: React.FC = () => {
  const frame = useCurrentFrame();
  const reveal = interpolate(frame, [15, 150], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
    easing: Easing.bezier(0.4, 0, 0.2, 1),
  });

  return (
    <AbsoluteFill
      style={{
        backgroundColor: "#000",
        justifyContent: "center",
        alignItems: "center",
      }}
    >
      <div style={{ opacity: reveal, scale: String(0.94 + 0.06 * reveal) }}>
        <Rings glow={reveal * 0.8} />
      </div>
    </AbsoluteFill>
  );
};

// Глава 9: финальный кадр — кольца, название, призыв подписаться, затемнение.
export const RingsFinal: React.FC<{ durationInFrames: number }> = ({ durationInFrames }) => {
  const frame = useCurrentFrame();
  const inO = interpolate(frame, [0, 40], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const subIn = interpolate(frame, [50, 90], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const fadeOut = interpolate(
    frame,
    [durationInFrames - 75, durationInFrames - 15],
    [1, 0],
    { extrapolateLeft: "clamp", extrapolateRight: "clamp" },
  );

  return (
    <AbsoluteFill
      style={{
        backgroundColor: "#000",
        justifyContent: "center",
        alignItems: "center",
        fontFamily: titleFont,
      }}
    >
      <div
        style={{
          opacity: inO * fadeOut,
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
          gap: 44,
        }}
      >
        <Rings glow={0.4} />
        <div
          style={{
            fontSize: 84,
            fontWeight: 600,
            color: COLORS.text,
            letterSpacing: "0.06em",
          }}
        >
          СОБРАНО В ЛЕГЕНДУ
        </div>
        <div
          style={{
            fontSize: 38,
            fontWeight: 300,
            color: COLORS.dim,
            letterSpacing: "0.12em",
            opacity: subIn,
          }}
        >
          Подпишись — впереди новые истории брендов
        </div>
      </div>
    </AbsoluteFill>
  );
};
