# eslee QuickSend

eslee QuickSend는 Windows 10/11과 Android 사이에서 파일을 같은 LAN으로 직접 전송하는 네이티브 앱입니다. 중앙 파일 서버, 계정, 클라우드 저장소를 사용하지 않습니다. 수신측이 영구 저장했다고 확인한 체크포인트를 기준으로 자동 재연결·재개하며, 기본 데이터 경로는 TLS로 암호화됩니다.

> **현재 v0.0.1은 시험용 Pre-release입니다.** 정식 안정 릴리즈가 아니며 실기기 재검증이 진행 중입니다.
> 대용량 전송과 반복 단절 복구는 아직 검증되지 않았으니 중요한 데이터에는 사용하지 마십시오.

## 다운로드

최신 빌드는 [v0.0.1 릴리즈](https://github.com/esleeeeee/eslee-quick-send/releases/tag/v0.0.1)에서 받을 수 있습니다.

| 파일 | 대상 |
| --- | --- |
| `eslee-QuickSend-Windows-x64-v0.0.1-Setup.exe` | **Windows 10/11 x64 — 권장** |
| `eslee-QuickSend-Windows-x64-v0.0.1-Portable.zip` | 설치하지 않고 시험하려는 경우 |
| `eslee-QuickSend-Android-v0.0.1-debug.apk` | Android — 시험용 debug 빌드 |
| `SHA256SUMS.txt` | 무결성 확인용 |

### Windows 설치형 (권장)

- 관리자 권한이 필요 없는 사용자별 설치이며 `%LOCALAPPDATA%\Programs\eslee QuickSend`에 설치됩니다.
- 시작 메뉴 바로가기, 선택형 바탕화면 바로가기, 제거 프로그램이 함께 설치됩니다.
- 설치 중 `Windows 시작 시 자동 실행`을 선택할 수 있습니다. 기본값은 켜짐이며 앱 설정에서도 바꿀 수 있습니다.
- 업데이트 설치와 기본 제거 모두 설정, 신뢰 기기, 전송 기록을 보존합니다.

### Windows 포터블

- 압축을 풀고 **그 폴더 안에서** `eslee QuickSend.exe`를 실행하십시오.
- EXE만 따로 옮기면 실행되지 않습니다. WinUI 리소스와 런타임이 같은 폴더에 있어야 합니다.

> 설치형과 포터블은 같은 사용자 데이터 경로를 사용합니다. 두 버전을 동시에 실행하지 마십시오.
> 포터블로 쓰던 신뢰 기기와 전송 기록은 설치형에서도 그대로 이어집니다.

### Android

- 시험 단계이므로 debug 서명 APK입니다. 설치 시 출처를 알 수 없는 앱 허용이 필요할 수 있습니다.
- 기존 앱 위에 덮어쓰기 설치하면 신뢰 기기, 수신 폴더 설정, 전송 기록이 유지됩니다.
- 처음 실행하면 받은 파일을 저장할 폴더를 먼저 지정해야 합니다.

## 사용 시 알아두기

- **창의 X 버튼은 종료가 아니라 트레이로 숨기기입니다.** 창을 닫아도 파일 수신 대기, 기기 발견, 진행 중인 전송이 그대로 유지됩니다.
- 완전히 종료하려면 시스템 트레이 아이콘의 `종료`를 사용하십시오. 전송 중이면 확인을 먼저 묻습니다.
- 자동 실행으로 시작하면 창 없이 트레이에서 바로 시작합니다.
- QuickSend는 하나의 프로세스만 실행됩니다. 다시 실행하면 기존 창이 열립니다.
- 두 기기를 처음 연결할 때는 양쪽에 표시되는 6자리 인증번호가 같은지 확인해야 합니다.

## 저장소 구성

```text
protocol/                 언어 중립 wire protocol 및 보안 규약
src/QuickSend.Core/       .NET 프로토콜·상태 머신·복구 핵심
src/QuickSend.Windows/    .NET 10 + WinUI 3 Windows 앱
android/                  Kotlin + Jetpack Compose Android 앱
tests/                    단위/통합/장애 주입 테스트
docs/                     아키텍처, 테스트, 요구사항 추적 문서
```

## 핵심 불변 조건

- 진행률과 재개 offset은 송신 socket의 전송량이 아니라 **수신측 durable checkpoint**만 사용합니다.
- 기본 청크 크기는 8 MiB이며 최대 8개 청크를 동시에 in-flight 상태로 유지합니다. 청크마다 ACK를 기다리는 stop-and-wait 방식이 아닙니다.
- 64 MiB 또는 1초 중 먼저 도달한 시점에 부분 파일을 flush하고 체크포인트를 영구 저장합니다.
- 연결이 끊기면 `2, 5, 10, 20, 30, 60…초` backoff로 계속 복구하며 네트워크·기기 재발견 이벤트가 오면 즉시 다시 시도합니다.
- 최종 파일명은 모든 청크 검증, 길이와 Merkle root 검증, durable flush가 끝난 후에만 확정합니다.
- 원본 metadata가 바뀌면 해당 파일을 `확인 필요`로 격리하고 변경되지 않은 파일은 계속합니다. 인증서 identity가 신뢰 DB와 다르면 데이터 요청 전에 연결을 거부합니다.

## 직접 빌드 (개발자용)

필요 도구는 .NET 10 SDK, Windows SDK, JDK 21, Android SDK입니다.

```powershell
dotnet build .\QuickSend.slnx
dotnet run --project .\tests\QuickSend.Core.Tests\QuickSend.Core.Tests.csproj

cd android
.\gradlew.bat testDebugUnitTest
.\gradlew.bat assembleDebug
```

WinUI 패키징과 Android 실기기 실행 절차는 [docs/BUILDING.md](docs/BUILDING.md), 장애·성능 검증 절차는 [docs/TESTING.md](docs/TESTING.md)를 참고하십시오.

실제로 수행한 검증과 아직 수행하지 못한 실기기/대용량/성능 시험은 [docs/VALIDATION.md](docs/VALIDATION.md)에 구분해 기록합니다.

## 보안 안내

최초 페어링 시 양쪽에 표시되는 6자리 코드를 비교해 신뢰를 승인합니다. 이후 인증서 공개키 지문으로 기기를 식별합니다. private key, 파일 내용, 페어링 비밀은 로그에 기록하지 않습니다. 인터넷 중계 기능은 v1에 없습니다.
