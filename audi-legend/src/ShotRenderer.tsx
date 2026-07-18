import React from "react";
import {
  AbsoluteFill,
  Img,
  Sequence,
  staticFile,
  useCurrentFrame,
  interpolate,
} from "remotion";
import type { TimedShot } from "./storyboard";
import { GradedFrame } from "./components/Grade";
import { KenBurns } from "./components/KenBurns";
import { Placeholder } from "./components/Placeholder";
import { TitleOverlay } from "./components/TitleOverlay";
import { RingsIntro, RingsMerge, RingsReveal, RingsFinal } from "./components/FourRings";
import { WordCard, MoneyScheme, LogoList, Vorsprung, PressureMap } from "./components/Cards";

const Photo: React.FC<{ id: string; image: string | null; visual: string; anchor?: string }> = ({
  id,
  image,
  visual,
  anchor,
}) => {
  if (image) {
    return (
      <Img
        src={staticFile(`photos/${image}`)}
        style={{ width: "100%", height: "100%", objectFit: "cover" }}
      />
    );
  }
  return <Placeholder id={id} visual={visual} anchor={anchor} />;
};

// Плавное появление кадра из чёрного.
const FadeIn: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const frame = useCurrentFrame();
  return (
    <AbsoluteFill
      style={{
        opacity: interpolate(frame, [0, 10], [0, 1], {
          extrapolateLeft: "clamp",
          extrapolateRight: "clamp",
        }),
        backgroundColor: "#000",
      }}
    >
      {children}
    </AbsoluteFill>
  );
};

export const ShotRenderer: React.FC<{ timed: TimedShot }> = ({ timed }) => {
  const { shot, chapter, globalIndex } = timed;
  const grade = shot.grade ?? chapter.grade;

  switch (shot.kind) {
    case "black":
      return <AbsoluteFill style={{ backgroundColor: "#000" }} />;

    case "photo":
      return (
        <FadeIn>
          <GradedFrame grade={grade}>
            <KenBurns durationInFrames={timed.duration} variant={globalIndex}>
              <Photo
                id={shot.id}
                image={shot.image ?? null}
                visual={shot.visual}
                anchor={shot.anchor}
              />
            </KenBurns>
          </GradedFrame>
          {shot.title ? <TitleOverlay text={shot.title} /> : null}
        </FadeIn>
      );

    case "montage": {
      const items = shot.montage ?? [];
      const per = Math.floor(timed.duration / Math.max(1, items.length));
      return (
        <FadeIn>
          <GradedFrame grade={grade}>
            {items.map((item, i) => (
              <Sequence
                key={item.id}
                name={`Монтаж: ${item.id}`}
                from={i * per}
                durationInFrames={i === items.length - 1 ? timed.duration - i * per : per}
              >
                <KenBurns durationInFrames={per} variant={globalIndex + i}>
                  <Photo id={item.id} image={item.image} visual={shot.visual} />
                </KenBurns>
              </Sequence>
            ))}
          </GradedFrame>
          {shot.title ? <TitleOverlay text={shot.title} /> : null}
        </FadeIn>
      );
    }

    case "rings-intro":
      return <RingsIntro />;
    case "rings-merge":
      return (
        <FadeIn>
          <RingsMerge />
          {shot.title ? <TitleOverlay text={shot.title} /> : null}
        </FadeIn>
      );
    case "rings-reveal":
      return (
        <FadeIn>
          <RingsReveal />
          {shot.title ? <TitleOverlay text={shot.title} /> : null}
        </FadeIn>
      );
    case "rings-final":
      return <RingsFinal durationInFrames={timed.duration} />;
    case "word-card":
      return <WordCard durationInFrames={timed.duration} />;
    case "money-scheme":
      return (
        <FadeIn>
          <MoneyScheme durationInFrames={timed.duration} />
        </FadeIn>
      );
    case "logo-list":
      return (
        <FadeIn>
          <LogoList />
        </FadeIn>
      );
    case "vorsprung":
      return <Vorsprung />;
    case "pressure-map":
      return (
        <FadeIn>
          <PressureMap />
        </FadeIn>
      );
    default:
      return null;
  }
};
