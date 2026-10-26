# 기준 앱 비교 장면 (fidelity sheet)

Snaplet 결과물의 완성도(화살표·도형·말풍선·번호·효과·배경 모양)를 원본과 나란히 비교하기 위한 고정 장면입니다.

- 바탕 이미지: 960×640 테스트 격자. `research/test-grid.png`와 픽셀 단위로 같으며 `FidelitySheet.TestGrid()`가 코드로 생성합니다.
  - 배경 `#F5F6FA`, 파란 사각형 (60,60)–(440,280) `#2069D2`, 주황 사각형 (520,60)–(900,280) `#F0AF28`, 체커 (60,360)–(900,580) 40px 칸 `#DC3C46`/`#FAFAFA`.
- 생성: `Snaplet.exe --self-test <폴더>`가 `fidelity-*.png`를 남깁니다. CI는 push마다 결과를 `visual-review` 브랜치에 올립니다(`git clone -b visual-review https://github.com/sabsalee/snaplet`).
- 각 장면은 Snaplet의 **기본(첫 번째) 프리셋**을 쓰며, 새 사용자가 처음 보는 결과입니다. 원본에서는 같은 좌표로 기본 설정 그대로 그립니다.

| 장면 | Snaplet 파일 | 내용 (이미지 좌표) |
| --- | --- | --- |
| S1 | `fidelity-s1-arrow-default.png` | 화살표 (155,148)→(680,245), 기본 프리셋. `research/reference-test-edited.png`의 원본 기본 화살표와 같은 위치 |
| S1b | `fidelity-s1-arrow-filled-red.png` | 같은 화살표, 원본처럼 빨강 `#FF0D00`·채운 몸통·굵기 4 |
| S2 | `fidelity-s2-arrow-presets.png` | 화살표 프리셋 4종 |
| S3 | `fidelity-s3-arrow-heads.png` | 머리 4종(채움·열린 곡선·가는 V·양방향) (100,y)→(420,y), y=100/220/340/460, 곡선 화살표 (560,120)→(860,500) 제어점 (860,160) |
| S4 | `fidelity-s4-shapes.png`, `-sketch.png` | 사각형 프리셋 4종 (90+200i,90)–(250+200i,220), 타원 채움 4종 (90+200i,390)–(250+200i,550) |
| S5 | `fidelity-s5-text.png` | 말풍선 프리셋 4종 "Snaplet test" |
| S6 | `fidelity-s6-counters-markers.png` | 번호 1–4 (140+120i,170), 형광펜 2종, 펜 |
| S7 | `fidelity-s7-effects.png`, `-spotlight.png` | 흐림 (80,380)–(300,560), 모자이크 (340,380)–(560,560), Erase (600,380)–(880,560), 돋보기, Spotlight 주황 사각형 |
| S8 | `fidelity-s8-backdrop*.png` | 배경 꾸미기 기본·투명 |

## 비교 결과 기록

| 장면 | 원본에서 관찰 | Snaplet 차이 | 조치 |
| --- | --- | --- | --- |
| S1 | 기본이 빨간 채운 리본 화살표, 몸통 폭 약 35px(960px 이미지 기준) | 기본 프리셋이 가는 열린 화살표(코랄, 굵기 2.5). 채운 화살표도 같은 굵기에서 몸통이 더 가늘다 | 원본 관찰 후 기본값·굵기 비율 조정 예정 |
