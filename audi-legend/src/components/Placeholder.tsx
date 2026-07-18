import React from "react";
import { AbsoluteFill } from "remotion";
import { titleFont, COLORS } from "../fonts";

// Кадр-заглушка: показывает, какой файл сюда положить и что должно быть на экране.
export const Placeholder: React.FC<{
  id: string;
  visual: string;
  anchor?: string;
}> = ({ id, visual, anchor }) => {
  return (
    <AbsoluteFill
      style={{
        backgroundColor: COLORS.card,
        justifyContent: "center",
        alignItems: "center",
        fontFamily: titleFont,
      }}
    >
      <AbsoluteFill
        style={{
          border: "3px dashed #3a3a42",
          margin: 50,
          width: "auto",
          height: "auto",
          inset: 50,
        }}
      />
      <div
        style={{
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
          gap: 36,
          maxWidth: 1400,
          textAlign: "center",
          padding: "0 120px",
        }}
      >
        <svg width="110" height="90" viewBox="0 0 110 90" fill="none">
          <rect x="4" y="18" width="102" height="68" rx="10" stroke="#4a4a52" strokeWidth="5" />
          <path d="M36 18l8-13h22l8 13" stroke="#4a4a52" strokeWidth="5" />
          <circle cx="55" cy="52" r="19" stroke="#4a4a52" strokeWidth="5" />
        </svg>
        <div style={{ fontSize: 50, fontWeight: 500, color: COLORS.text, lineHeight: 1.25 }}>
          {visual}
        </div>
        {anchor ? (
          <div style={{ fontSize: 32, fontWeight: 300, color: COLORS.dim, fontStyle: "italic" }}>
            «{anchor}»
          </div>
        ) : null}
        <div
          style={{
            fontSize: 34,
            fontWeight: 400,
            color: "#e3b341",
            fontFamily: "Consolas, monospace",
            backgroundColor: "#1d1d22",
            padding: "14px 30px",
            borderRadius: 8,
          }}
        >
          public/photos/{id}.jpg
        </div>
      </div>
    </AbsoluteFill>
  );
};
