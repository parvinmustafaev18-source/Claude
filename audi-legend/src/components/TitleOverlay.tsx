import React from "react";
import { useCurrentFrame, interpolate, Easing } from "remotion";
import { titleFont, COLORS } from "../fonts";

// Единый стиль титров: плашка слева внизу, появляется с задержкой.
export const TitleOverlay: React.FC<{ text: string }> = ({ text }) => {
  const frame = useCurrentFrame();
  const appear = interpolate(frame, [10, 34], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
    easing: Easing.bezier(0.16, 1, 0.3, 1),
  });

  return (
    <div
      style={{
        position: "absolute",
        left: 90,
        bottom: 100,
        opacity: appear,
        translate: `0px ${(1 - appear) * 24}px`,
        backgroundColor: "rgba(0,0,0,0.6)",
        borderLeft: `6px solid ${COLORS.accent}`,
        padding: "16px 34px 18px 28px",
        fontFamily: titleFont,
        fontSize: 46,
        fontWeight: 500,
        letterSpacing: "0.04em",
        color: COLORS.text,
      }}
    >
      {text}
    </div>
  );
};
