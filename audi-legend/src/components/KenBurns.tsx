import React from "react";
import { AbsoluteFill, useCurrentFrame, interpolate, Easing } from "remotion";

// Медленный зум/панорама по статичному кадру (эффект Кена Бёрнса).
// Направление чередуется по индексу кадра в фильме.
export const KenBurns: React.FC<{
  durationInFrames: number;
  variant: number;
  children: React.ReactNode;
}> = ({ durationInFrames, variant, children }) => {
  const frame = useCurrentFrame();
  const t = interpolate(frame, [0, durationInFrames], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
    easing: Easing.bezier(0.4, 0, 0.6, 1),
  });

  // Амплитуда зума растёт с длительностью кадра, но не бесконечно.
  const zoomAmp = Math.min(0.16, 0.05 + durationInFrames / 6000);
  const panAmp = 28;

  const v = variant % 4;
  const scale =
    v === 1 ? 1 + zoomAmp - zoomAmp * t : 1 + zoomAmp * t;
  const panX =
    v === 2 ? -panAmp + 2 * panAmp * t : v === 3 ? panAmp - 2 * panAmp * t : 0;

  return (
    <AbsoluteFill
      style={{
        scale: String(scale),
        translate: `${panX}px 0px`,
      }}
    >
      {children}
    </AbsoluteFill>
  );
};
