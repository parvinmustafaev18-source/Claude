import React from "react";
import { AbsoluteFill, useCurrentFrame, useVideoConfig, interpolate } from "remotion";
import { titleFont, COLORS } from "../fonts";

// Заставка-разделитель между главами: 2 секунды чёрного с номером главы.
export const ChapterDivider: React.FC<{ n: number; name: string }> = ({ n, name }) => {
  const frame = useCurrentFrame();
  const { durationInFrames } = useVideoConfig();
  const opacity = interpolate(
    frame,
    [0, 12, durationInFrames - 10, durationInFrames],
    [0, 1, 1, 0],
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
          opacity,
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
          gap: 18,
        }}
      >
        <div
          style={{
            fontSize: 30,
            fontWeight: 300,
            letterSpacing: "0.5em",
            color: COLORS.dim,
            textTransform: "uppercase",
          }}
        >
          Глава {n}
        </div>
        <div style={{ fontSize: 64, fontWeight: 500, color: COLORS.text }}>{name}</div>
        <div style={{ width: 120, height: 3, backgroundColor: COLORS.accent, marginTop: 12 }} />
      </div>
    </AbsoluteFill>
  );
};
