import { Composition } from "remotion";
import { Main } from "./Main";
import { FPS, TOTAL_FRAMES } from "./storyboard";

export const MyComposition = () => {
  return (
    <Composition
      id="AudiLegend"
      component={Main}
      durationInFrames={TOTAL_FRAMES}
      fps={FPS}
      width={1920}
      height={1080}
    />
  );
};
