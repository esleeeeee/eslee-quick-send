# 빌드와 실행

## 필요 도구

- Windows 10 1809 이상 또는 Windows 11 x64
- .NET SDK 10.0.301 이상
- JDK 21
- Android SDK Platform 36, Build Tools 36.0.0, Platform Tools
- Android Gradle Plugin 9.2.0과 Gradle wrapper 9.4.1은 저장소 설정에 고정됨

## Windows

```powershell
dotnet restore .\QuickSend.slnx
dotnet build .\QuickSend.slnx
dotnet run --project .\src\QuickSend.Windows\QuickSend.Windows.csproj
```

출력은 `src/QuickSend.Windows/bin/Debug/net10.0-windows10.0.26100.0/win-x64/`에 생성된다. 앱은 unpackaged, self-contained Windows App SDK 방식이다. 최초 실행 시 방화벽의 사설 네트워크 허용이 필요할 수 있다. 수신 기본 위치는 `%USERPROFILE%\Downloads\eslee QuickSend`다.

## Android

`android/local.properties`에 설치한 SDK 경로를 지정한다. 이 파일은 저장소에 포함하지 않는다.

```properties
sdk.dir=C\:\\Users\\me\\AppData\\Local\\Android\\Sdk
```

```powershell
cd .\android
.\gradlew.bat testDebugUnitTest assembleDebug
```

APK는 `android/app/build/outputs/apk/debug/app-debug.apk`에 생성된다. 설치 후 알림, 주변 기기, 로컬 네트워크 권한을 허용하고 수신 폴더를 한 번 선택한다. 포그라운드 서비스 알림이 표시되는 동안 화면이 꺼져도 운영체제의 정식 connected-device 실행 경로로 전송을 유지한다.

현재 프로젝트처럼 전체 경로에 한글이 있을 때 AGP 9.2의 JVM test worker가 테스트 클래스를 찾지 못할 수 있다. 앱 컴파일과 무관한 경로 문제이며, 영문 junction에서 실행하면 된다.

```powershell
New-Item -ItemType Junction -Path C:\qs-eslee-android -Target (Resolve-Path .\android)
cd C:\qs-eslee-android
.\gradlew.bat testDebugUnitTest assembleDebug
```

## 최초 연결

두 기기를 같은 LAN에 연결하고 양쪽 앱을 연다. 주변 기기를 선택해 파일 또는 폴더를 고르면 최초 한 번 6자리 번호가 양쪽에 표시된다. 번호가 같을 때만 `신뢰`를 누른다. 이후 연결은 저장한 인증서 공개키 지문으로 인증된다.
