# 테스트 지침

## 자동 테스트

```powershell
dotnet run --project .\tests\QuickSend.Core.Tests\QuickSend.Core.Tests.csproj

cd C:\qs-eslee-android
.\gradlew.bat clean testDebugUnitTest assembleDebug
```

코어 테스트는 frame/chunk roundtrip, 64-bit 크기, payload 할당 상한, 상태 전이, checkpoint 시간·바이트 조건, backoff, Merkle snapshot, 경로 탈출 방지, 마지막 부분 청크 재개, durable disconnect 재개, uncommitted rollback, 손상 청크 거부를 검사한다. Android JVM 테스트는 64-bit header, Merkle 재개, checkpoint 정책, backoff를 독립 구현에서 검사한다.

## 실기기 필수 매트릭스

다음 항목은 Windows PC와 Android 기기가 모두 있어야 완료 판정할 수 있다.

| 시나리오 | 확인 사항 |
|---|---|
| Wi-Fi 30초 차단 | UI가 복구 중으로 바뀌고 같은 committed offset부터 재개 |
| Android 화면 OFF 30분 | foreground 알림 유지, wake/Wi-Fi lock, 완료 파일 일치 |
| Android 앱 프로세스 종료·재시작 | DB/SAF partial을 찾아 재개 |
| Windows 앱 완전 종료·재시작 | DB/partial을 찾아 재개 |
| Windows X 버튼 | 활성 전송은 tray에서 계속, tray 종료는 확인 표시 |
| 10/50/100 GiB 단일 파일 | 64-bit offset, 메모리 안정, 최종 root 일치 |
| 다중 파일 중 원본 변경 | 변경 파일만 `확인 필요`, 나머지 완료 |
| 저장 공간 부족 | 사용자 메시지, partial 보존, 공간 확보 후 재개 |
| 이름 충돌 | 기존 파일 비덮어쓰기 및 새 이름 확정 |
| 신뢰 해제/인증서 변경 | 기존 신뢰로 연결하지 않음 |

속도 비교는 같은 AP, 같은 거리, 같은 파일, 같은 기기 상태에서 Quick Share와 각각 최소 5회 실행해 평균·중앙값·peak·안정 구간 throughput을 기록한다. 측정하지 않은 값은 0이나 추정치로 채우지 않는다.

## 장애 주입 확장점

현재 deterministic 테스트는 청크 손상, 연결 종료 후 재생성, checkpoint 전 크래시를 in-process로 재현한다. 실제 socket ACK drop/delay, N MiB 후 RST, DB 지연, 무작위 단절은 별도 debug transport decorator로 추가할 수 있으며 release UI에는 노출하지 않는다.
