# 캡처 시작 지연 확인

영역 캡처는 매번 WGC 세션을 시작하는 대신 **GDI BitBlt → top-down 32-bit DIB → 소유된 BGRA32 이미지**로 현재 모니터를 찍는다. 상시 화면 캐시를 저장하지 않는다. GDI가 Win32 오류를 반환하면 WGC로 명시적으로 재시도하며, 미리보기 상태와 성능 로그에는 실제 사용한 엔진이 기록된다. 창 단독 캡처는 WGC를 유지한다.

이 변경은 Windows 성능 실측 전이다. 기존 1~2초 중 WGC 시작 비용이 차지하는 비중과 실제 개선 폭을 단정하지 않는다. 일반 SDR 데스크톱을 위한 빠른 경로이며 HDR 색상 보존은 지원하지 않는다.

## A/B 비교

앱을 트레이에서 완전히 종료한 다음 PowerShell에서 실행한다. 기본값/`auto`는 GDI 우선, 실패 시 WGC. `gdi`는 GDI만 사용하고 실패를 표시한다. `wgc`는 이전 WGC 경로다. 환경변수는 해당 PowerShell에서 시작한 프로세스에만 적용된다.

```powershell
$env:SNAPLET_CAPTURE_ENGINE = 'wgc'
.\Snaplet.exe
# 비교 후 트레이에서 종료
$env:SNAPLET_CAPTURE_ENGINE = 'gdi'
.\Snaplet.exe
# 기본값 복구 전에도 실행 중 앱을 종료할 것
Remove-Item Env:SNAPLET_CAPTURE_ENGINE
```

로그: `%LOCALAPPDATA%\Snaplet\logs\capture-performance.jsonl` (약 1MiB마다 `.1` 파일 한 개로 회전, 두 파일 약 2MiB). 단축키·메뉴 호출마다 한 줄. 이미지/픽셀/OCR/창 제목/저장 경로를 넣지 않으며 네트워크로 전송하지 않는다. 파일 쓰기는 백그라운드에서 실행한다.

- `source: hotkey`: WM_HOTKEY 처리 시점부터 측정. `ui`: 명령 진입 시점부터 측정.
- `commandEnteredMs`, `ownWindowsHiddenMs`: 명령 진입 및 자체 창 숨김 요청 처리까지.
- `snapshotsReadyMs`: 모든 모니터의 현재 이미지가 준비된 시점.
- `overlayCreateStartedMs`, `overlaysShownMs`, `overlayContentRenderedMs`: 선택창 생성 시작/Show 완료/전 모니터 ContentRendered까지.
- `monitors`: 크기, 실제 엔진, 모니터별 캡처 소요시간, GDI 실패 후 fallback이라면 Win32 오류 번호. 모니터 이름이나 화면 내용은 기록하지 않는다.
- `outcome: overlay-rendered`: 주 비교 지표는 `overlayContentRenderedMs`. 이 값은 사용자가 영역을 선택하는 대기 시간을 포함하지 않는다. 그 전에 취소·실패했다면 다른 outcome이 기록되고 해당 값은 없다.

WPF ContentRendered는 UI 렌더링 알림이므로 실제 모니터 스캔아웃 지연이나 첫 마우스 응답을 엄밀히 측정한 값은 아니다. 첫1회/연속30회/유휴뒤를 나누고, 동일 모니터·배율·화면 상태에서 median/p95와 이미지 정확성을 비교한다. 기본 목표는 반복 median≤100ms, p95≤150~200ms이지만 아직 검증된 수치가 아니다.

## 검증 범위

Windows `--self-test`는 사용자 화면을 읽지 않고 합성2×2 DIB를 만든 뒤 실제 네이티브 BitBlt 경로를 실행한다. 픽셀 순서·불투명 알파·네이티브 메모리 해제 후 이미지 소유권·호출 전 취소를 검사하고 `gdi-smoke.txt`를 남긴다. 이 테스트는 Mac에서 실행할 수 없다.

실제 Windows에서는 단축키→선택창 지연 외에 자체 창 숨김 반영, 가속 영상·투명 창, 캡처 오버레이 비혼입, 커서, 혼합DPI/음수 좌표/세로 회전/다중GPU, 해상도 변경·잠금·절전복귀를 확인한다. GDI가 오류 대신 검정 픽셀을 반환하는 콘텐츠는 자동 감지하지 않는다. 보호 콘텐츠·보안 데스크톱 캡처는 보장하지 않는다. 모든 모니터 snapshot은 병렬이므로 움직이는 화면에서 완전히 동일한 시간의 프레임이라고 보장하지 않는다.

## 구현 근거

- [Microsoft BitBlt](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-bitblt): CAPTUREBLT로 layered window 포함, 색상 관리 미수행.
- [Microsoft CreateDIBSection](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-createdibsection): 직접 픽셀 접근, DeleteObject 해제, 읽기 전 GDI 동기화 요구.
- [Microsoft GdiFlush](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-gdiflush): 같은 worker의 GDI 작업 flush 후 CPU/WPF 이미지 복사.
