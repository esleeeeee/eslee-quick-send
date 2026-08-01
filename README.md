# eslee QuickSend

eslee QuickSend는 Windows 10/11과 Android 사이에서 파일을 같은 LAN으로 직접 전송하는 네이티브 앱입니다. 중앙 파일 서버, 계정, 클라우드 저장소를 사용하지 않습니다. 수신측이 영구 저장했다고 확인한 체크포인트를 기준으로 자동 재연결·재개하며, 기본 데이터 경로는 TLS로 암호화됩니다.

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

## 빠른 빌드

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
