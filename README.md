# Snaplet

Windows 11 x64용 로컬 화면 캡처·이미지 편집 앱입니다. 단축키로 캡처하고 작은 미리보기에서 편집·복사·저장·OCR·고정으로 이어갑니다.

**[다운로드 및 변경 사항](https://github.com/sabsalee/snaplet/releases)** · [설치·사용 안내](docs/START-HERE.ko.md) · [자동 업데이트](docs/UPDATING.md)

![Snaplet](docs/screenshots/home.png)

## 설치

1. Releases에서 최신 **Setup.exe** 파일을 받습니다.
2. 기존 Snaplet을 트레이에서 종료한 뒤 설치 파일을 실행합니다. .NET 런타임이 포함되며 사용자 계정에 설치됩니다.
3. 이후 새 버전은 자동 확인·다운로드하고 **설정 → 업데이트 → 업데이트하고 다시 시작**으로 적용합니다.

0.6 이하 ZIP 버전을 사용했다면 이번에 설치 버전으로 한 번 전환하세요. 기존 단축키·저장 설정은 이어서 사용합니다. `Snaplet-Windows-x64.zip`도 제공하지만 ZIP 실행 버전은 자동 교체하지 않습니다.

개발 버전은 전자서명하지 않았습니다. Windows ARM·macOS·Linux는 지원하지 않습니다.

## 사용

| 단축키 | 작업 |
| --- | --- |
| Ctrl+Shift+1 | 영역 캡처 |
| Ctrl+Shift+2 | 활성 창 캡처 |
| Ctrl+Shift+3 | 현재 모니터 캡처 |
| Ctrl+Shift+4 | 3초 후 캡처 |
| Ctrl+Shift+5 | 최근 캡처 편집 |
| Ctrl+Shift+6 | 직전 영역 반복 |
| (설정에서 지정) | 모든 화면 · 창 선택 · 스크롤 · 텍스트/QR 바로 인식 · Snaplet 열기 |

영역 선택 화면: 확대경으로 픽셀 좌표·색 확인 · Tab 색 복사 · R 측정자 · Shift 정사각형 · Space 이동 · Ctrl을 누른 채 놓으면 조절점·방향키·[ ]·A(내용에 맞춤)로 다듬고 Enter.

편집창의 **프리셋**에서 스타일을 고르고, 객체를 가리키면 나타나는 경계·커서로 편집 위치를 확인하세요. 파란 원은 말꼬리, 마름모는 곡률·둥글기 조절점입니다. 속성 수치는 직접 입력한 뒤 Enter로 적용합니다.

미리보기에서 편집·복사·저장·OCR·고정을 선택합니다. 단축키와 캡처 후 동작은 설정에서 바꿀 수 있습니다. 앱 종료는 트레이 메뉴를 이용하세요.

- 화살표·곡선·말풍선·번호·도형·펜, 형광펜·모자이크·흐림·Erase·Spotlight·돋보기.
- 도형 말꼬리 핸들, 색·두께·채움, 이미지 추가와 크기·불투명도 조절, 자르기·Undo/Redo.
- Ctrl+휠은 포인터 기준 확대. Space/오른쪽 드래그는 이동. 방향키는 1px, Shift+방향키는 10px 이동.
- 배경 꾸미기(그라데이션·여백·그림자·둥근 모서리), 스포이트와 WCAG/APCA 대비 검사, 내보내기 크기.
- 한글·영문 로컬 OCR과 QR 인식, PNG/JPEG 내보내기, 자동·수동 스크롤 캡처(고정 헤더·푸터 처리).
- 포인터 포함, 창 캡처 그림자, 고정 이미지 불투명도·클릭 통과, `Snaplet.exe --command region`.

캡처 이미지와 OCR은 PC 안에서 처리합니다. 자동 업데이트를 켜면 GitHub에 버전 정보와 업데이트 파일을 요청하며 캡처 이미지·OCR 내용은 보내지 않습니다. 편집 객체는 메모리에 보관하므로 종료·업데이트 전에 필요한 결과를 저장하세요(최근 캡처 원본은 디스크 보관 옵션이 있습니다). 영구 프로젝트 저장, HDR 색 보존, 여러 모니터에 걸친 선택은 아직 지원하지 않습니다.

[편집 도구](docs/editor-tools.ko.md) · [추가 편집 도구](docs/advanced-tools.ko.md) · [화면 갤러리](docs/design-gallery.ko.md) · [로드맵·기준 앱 대비표](docs/ROADMAP.ko.md)

## 개발

.NET SDK 10.0.402가 필요합니다. 실제 앱 실행·패키징은 Windows에서 수행합니다.

```powershell
./scripts/build-windows.ps1
./scripts/test-windows.ps1 -Ocr
```

빌드는 공통 로직 검사, Windows WPF 화면·조작 검사, ZIP과 Velopack 설치/업데이트 패키지를 생성합니다. GitHub Actions는 별도 Windows 설치 → 다운로드 검증 → 실제 버전 교체·재시작·설정 보존 검사도 실행합니다.

`v` 버전 태그를 푸시하면 검사가 통과한 경우에만 GitHub Releases에 게시합니다. 버전은 프로젝트의 `<Version>`과 일치해야 합니다. [배포 절차](docs/UPDATING.md), [검증 범위](docs/TESTING.md), [Windows 실사용 체크리스트](docs/runtime-validation.md)를 참고하세요.

의존성 출처와 라이선스는 [Third-party notices](docs/THIRD-PARTY-NOTICES.md)에 있습니다.
