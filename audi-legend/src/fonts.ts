import { loadFont } from "@remotion/google-fonts/Oswald";

const oswald = loadFont("normal", {
  weights: ["300", "400", "500", "600"],
  subsets: ["cyrillic", "latin"],
});

export const titleFont = `${oswald.fontFamily}, sans-serif`;

export const COLORS = {
  bg: "#0a0a0b",
  card: "#131316",
  text: "#e8e8ea",
  dim: "#8f8f96",
  accent: "#bb0a30",
  silver: "#d9d9de",
};
