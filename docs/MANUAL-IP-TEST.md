# 수동 IP 연결 실기기 테스트

환경 기준:

- Windows PC: `192.168.123.102`
- Android phone: `192.168.123.100`
- QuickSend TCP port: `41231`

## 연결 및 페어링

1. Windows에서 새 QuickSend 빌드를 실행한다.
2. Windows 진단 로그의 `listener.bound.address`, `listener.bound.port`, `listener.dual_mode`를 확인한다.
3. Android에 새 debug APK를 설치하고 QuickSend를 실행한다.
4. 주변 기기 영역에서 **IP 주소로 직접 연결**을 누른다.
5. IP 주소에 `192.168.123.102`, Port에 `41231`을 입력하고 **연결**을 누른다.
6. 양쪽에 표시되는 SAS 인증번호가 같은지 확인하고 양쪽에서 신뢰를 선택한다.
7. Android 기기 목록에 Windows peer가 나타나고 선택되는지 확인한다.

## 파일 전송

1. 수동 연결된 Windows peer가 선택된 상태에서 Android의 파일 버튼을 누른다.
2. 약 100 MB 테스트 파일을 선택한다.
3. Windows의 `Downloads\eslee QuickSend`에 파일이 생성되고 전송 완료 및 무결성 검증이 표시되는지 확인한다.

## 실패 시 수집할 로그

- Android: 앱 내부 `files/logs/quicksend.ndjson`의 `manual_connect.*`, `android.nsd.*` 이벤트
- Windows: `%LOCALAPPDATA%\eslee\QuickSend\logs\quicksend.ndjson`의 `listener.*`, `incoming.session.*` 이벤트

`manual_connect.tcp.success` 후 TLS 실패면 보안 handshake 문제, `manual_connect.tls.success` 후 protocol 실패면 HELLO/버전/페어링 문제다. TCP 단계에서 실패하면 QuickSend 실행 상태, IP, port 또는 방화벽을 확인한다.
