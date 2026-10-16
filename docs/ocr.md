# 포터블 한국어·영어 OCR

조사·구현 기준일: 2026-10-15. **코드는 구현했으며 Windows에서의 실제 인식 성공·정확도·속도는 아직 검증하지 않았다.**

포터블 배포는 화면 속 글자를 PC 안에서 Tesseract로 인식한다. MSIX 패키지 ID, Windows OCR 언어팩, 인터넷 연결 없이 사용할 수 있도록 한국어·영어 모델을 함께 배포한다. 이미지와 인식 문자열을 외부 서버로 보내지 않는다.

## 고정한 엔진과 모델

.NET 연결은 [charlesw의 NuGet `Tesseract` 5.2.0](https://www.nuget.org/packages/Tesseract/5.2.0)을 사용한다. 이는 Tesseract 프로젝트 자체의 공식 .NET SDK가 아니라 공개 소스의 별도 래퍼다. [래퍼 원본](https://github.com/charlesw/tesseract/tree/5.2.0)은 Apache-2.0이다. `Tesseract.Drawing`이나 System.Drawing 의존성은 추가하지 않았다.

한국어 `kor.traineddata`와 영어 `eng.traineddata`는 [Tesseract 프로젝트의 tessdata_fast 저장소](https://github.com/tesseract-ocr/tessdata_fast/tree/87416418657359cb625c412a48b6e1d6d41c29bd)에서 받은 파일이다. 이 모델은 LSTM 전용이므로 `EngineMode.LstmOnly`를 사용한다. 모델은 Apache-2.0이며 배포 폴더에 LICENSE를 동봉한다. 빠른 모델은 속도와 정확도를 절충한다. 인식 결과는 이미지 품질과 글자 크기에 따라 달라진다.

고정 리비전: `87416418657359cb625c412a48b6e1d6d41c29bd`.

| 파일 | 바이트 | SHA256 |
| --- | ---: | --- |
| eng.traineddata | 4,113,088 | `7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2` |
| kor.traineddata | 1,677,415 | `6b85e11d9bbf07863b97b3523b1b112844c43e713df8b66418a081fd1060b3b2` |

원본 URL과 LICENSE 해시는 `assets/tessdata/provenance.json`에 기록했다. 모델 업데이트 시 움직이는 main URL을 그대로 배포하지 말고 새로운 커밋·해시·인식 결과를 함께 확인해야 한다.

## 배포 시 필요한 파일

실행 파일 옆의 `tessdata` 폴더에 모델 2개, LICENSE, provenance.json을 복사한다. `assets/licenses`에는 고정 태그 원본에서 받은 래퍼·Tesseract 5.2.0의 Apache-2.0, Leptonica 1.82.0의 BSD 계열 라이선스와 출처·해시 manifest를 보관한다. 배포 시 `licenses` 폴더로 함께 복사한다. NuGet 패키지의 `Tesseract.dll`, 의존 managed DLL, `x64/leptonica-1.82.0.dll`, `x64/tesseract50.dll`도 포함한다. 이 네이티브 파일 이름과 하위 폴더는 [5.2.0 패키지 빌드 설정](https://github.com/charlesw/tesseract/blob/5.2.0/src/Tesseract/Tesseract.targets)에서 확인했다. 실제 ZIP의 파일 존재까지 검사해야 한다. 복원된 NuGet 5.2.0의 두 DLL은 PE 헤더 machine `0x8664`(x64)를 확인했다. import table에서 leptonica의 `VCRUNTIME140.dll`, tesseract의 `MSVCP140.dll`·`VCRUNTIME140.dll`·`VCRUNTIME140_1.dll` 의존성을 확인했다. 이는 정적 파일 확인이며 DLL 로드 성공의 실행 증거는 아니다.

래퍼는 [VS2019 네이티브 빌드와 VC++ 런타임 필요성](https://github.com/charlesw/tesseract/tree/5.2.0#dependencies)을 명시한다. Windows 11 x64 사용자는 [Microsoft의 최신 Visual C++ v14 x64 Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)을 설치해야 할 수 있다. .NET self-contained 배포는 VC++ 런타임까지 포함한다는 뜻이 아니다. 런타임 설치 파일을 자동 실행하지 않는다. 따라서 현 배포를 '모든 Windows에서 추가 설치 없이 OCR 가능'이라고 설명하면 안 된다.

## 구현한 처리와 한계

`TesseractOcr.RecognizeAsync`가 호출 스레드에서 원본을 고정한 뒤 작업 스레드에서 인식한다. 네이티브 엔진은 한 요청이 소유하고 한 번에 한 요청만 실행한다. 각 타일의 iterator → Page → Pix를 차례로 해제한 뒤 엔진을 해제한다. [래퍼의 이미지 처리 수명 규칙](https://github.com/charlesw/tesseract/blob/5.2.0/src/Tesseract/TesseractEngine.cs)에 맞춰 Page가 살아 있는 동안 다음 이미지를 처리하지 않는다.

이미지를 축소하지 않고 최대 2560×2560 픽셀 타일과 160픽셀 겹침으로 나눈다. 투명 영역은 흰색으로 합성하고 PNG 메모리 버퍼로 네이티브 엔진에 전달한다. 전체 1억 픽셀을 초과하면 필요한 부분을 자르도록 안내한다. 한 타일의 버퍼와 모델 메모리는 추가로 사용하며 실제 메모리 측정은 아직 하지 않았다.

타일 경계의 줄은 원래 이미지 좌표를 붙여 비교하고, 겹치는 위치의 동일 문자열이나 잘린 부분 문자열을 제거한다. 결과는 위→아래, 왼쪽→오른쪽으로 정렬한다. 이 규칙은 표·다단 문서·경계에서 끊긴 긴 줄을 완벽히 복원하지 않는다. 중복이나 누락이 남을 수 있으므로 인식 결과 창에서 수정할 수 있어야 한다.

취소는 대기 중이거나 타일 사이에 확인한다. 네이티브 인식 중인 타일을 강제 중단하거나 해제하지 않는다. 따라서 취소 반응은 현재 타일 인식이 끝난 뒤 발생할 수 있다. 초기화 실패, 누락 모델, DLL/런타임/아키텍처 문제는 성공한 빈 결과로 숨기지 않고 오류를 반환한다.

## Windows에서 남은 확인

- ZIP을 새 Windows 11 x64 사용자 환경에서 풀고 VC++ 런타임 유무에 따른 초기화 및 오류 표시 확인.
- 8–16px 한국어, 영어, 혼합 글자·숫자·코드, 어두운 배경에서 CER/WER 및 시간 기록.
- 작은 캡처, 4K 캡처, 긴 스크롤 이미지, 타일 경계의 긴 줄에서 중복·누락 확인.
- 투명 PNG, 취소·연속 요청·창 닫기 중 모델 수명과 메모리 확인.
- 모든 배포 모델을 provenance 해시와 대조하고 실제 publish ZIP에서 x64 DLL·라이선스·모델 존재 확인.
