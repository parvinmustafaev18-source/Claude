import React from "react";
import { AbsoluteFill } from "remotion";
import type { Grade } from "../storyboard";

const FILTERS: Record<Grade, string> = {
  color: "saturate(1.05)",
  sepia: "grayscale(1) sepia(0.5) brightness(0.95) contrast(1.05)",
  bw: "grayscale(1) contrast(1.08)",
  muted: "saturate(0.8) contrast(1.03)",
};

// Цветообработка эпохи + виньетка поверх кадра.
export const GradedFrame: React.FC<{
  grade: Grade;
  children: React.ReactNode;
}> = ({ grade, children }) => {
  return (
    <AbsoluteFill style={{ backgroundColor: "#000" }}>
      <AbsoluteFill style={{ filter: FILTERS[grade] }}>{children}</AbsoluteFill>
      <AbsoluteFill
        style={{
          background:
            "radial-gradient(ellipse at center, rgba(0,0,0,0) 55%, rgba(0,0,0,0.55) 100%)",
          pointerEvents: "none",
        }}
      />
    </AbsoluteFill>
  );
};
