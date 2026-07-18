import React from "react";
import { AbsoluteFill, Sequence, staticFile } from "remotion";
import { Audio } from "@remotion/media";
import { buildTimeline, audioConfig } from "./storyboard";
import { ShotRenderer } from "./ShotRenderer";
import { ChapterDivider } from "./components/ChapterDivider";

const timeline = buildTimeline();

export const Main: React.FC = () => {
  return (
    <AbsoluteFill style={{ backgroundColor: "#000" }}>
      {timeline.shots.map((timed) => (
        <Sequence
          key={timed.shot.id}
          name={`${timed.chapter.n}.${timed.indexInChapter + 1} «${timed.shot.anchor.slice(0, 40)}»`}
          from={timed.from}
          durationInFrames={timed.duration}
        >
          <ShotRenderer timed={timed} />
        </Sequence>
      ))}
      {timeline.dividers.map((d) => (
        <Sequence
          key={`divider-${d.chapter.n}`}
          name={`Глава ${d.chapter.n}: ${d.chapter.name}`}
          from={d.from}
          durationInFrames={d.duration}
        >
          <ChapterDivider n={d.chapter.n} name={d.chapter.name} />
        </Sequence>
      ))}
      {audioConfig.voiceover ? (
        <Audio src={staticFile(audioConfig.voiceover)} />
      ) : null}
      {audioConfig.music ? (
        <Audio src={staticFile(audioConfig.music)} volume={audioConfig.musicVolume} />
      ) : null}
    </AbsoluteFill>
  );
};
