# 설치와 업데이트

Releases에서 Setup.exe를 실행하면 사용자 계정의 `%LOCALAPPDATA%\SnapletApp`에 설치합니다. .NET은 포함되며 필요한 Visual C++ 런타임은 설치 프로그램에서 확인합니다. 기존 설정은 별도의 `%LOCALAPPDATA%\Snaplet`에 있어 업데이트 시 유지됩니다.

기존 ZIP 버전에는 업데이트 코드가 없으므로 설치 파일로 한 번 전환해야 합니다. ZIP 버전을 트레이에서 종료하고 설치하세요. ZIP도 계속 제공하지만 그 파일을 실행한 앱은 스스로 교체하지 않습니다.

설정 → 업데이트에서 자동 확인·다운로드를 끌 수 있습니다. 기본값은 시작 15초 후와 6시간마다 확인하고 새 버전을 받습니다. 재시작은 사용자 선택이며, 캡처·복사·저장이 진행 중이면 적용하지 않습니다. 최근 캡처와 저장하지 않은 편집은 다시 시작하면 사라지므로 먼저 내보내세요.

네트워크 확인은 GitHub 릴리스와 업데이트 파일에만 사용합니다. 앱에 GitHub 토큰을 포함하지 않고 캡처 이미지나 OCR 내용을 업로드하지 않습니다. 현재 개발 릴리스도 업데이트 대상으로 사용합니다.

## 유지보수 배포

1. `src/Capture.Windows/Capture.Windows.csproj`의 `<Version>`과 변경 사항을 수정합니다.
2. 커밋을 푸시하고 같은 버전의 `v0.7.1` 형식 태그를 푸시합니다.
3. Windows 빌드·편집·UI·OCR·업데이트 상태 검사와 실제 설치/교체/재시작 검사가 통과한 경우에만 릴리스 작업이 게시합니다.

설치 파일, full/delta 패키지, `releases.win-x64.json`, `SHA256SUMS`를 함께 배포합니다. 이전 릴리스가 있으면 가져와 차등 패키지를 만들고, 실패하면 전체 패키지를 사용합니다. 앱 ID `SnapletApp`과 채널 `win-x64`는 기존 설치 호환성을 위해 유지합니다. 서명 인증서가 준비되기 전까지 개발 빌드는 미서명입니다.

기술 기반: [Velopack 통합](https://docs.velopack.io/integrating/overview), [GitHub 업데이트 소스](https://docs.velopack.io/integrating/update-sources), [Windows 설치 구조](https://docs.velopack.io/packaging/operating-systems/windows). 라이브러리와 CLI는 같은 버전으로 고정합니다.
