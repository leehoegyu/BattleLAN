# Battle.LAN

## 프로젝트 개요

이 프로젝트는 기존 **Battle.LAN** 애플리케이션을 분석하여 C# WPF로 재작성한 프로젝트입니다.

## 시스템 요구사항

- .NET 5.0 Runtime
- Visual Studio 2019 (빌드 환경)
- 관리자 권한 (Raw 소켓 사용을 위해 필요)

## 프로젝트 구조

```
BattleLAN/
  ├── Properties/
  │   └── AssemblyInfo.cs
  ├── Utils/
  │   └── VirtualLAN.cs
  ├── Forms/
  │   ├── MainWindow.xaml
  │   └── MainWindow.xaml.cs
  ├── App.xaml
  ├── App.xaml.cs
  ├── Styles.xaml
  └── BattleLAN.csproj
```

## 사용 방법

1. **IP 주소 추가**
   - IP 주소 입력 필드에 수신자 IP 주소를 입력합니다 (예: 192.168.1.100)
   - "Add" 버튼을 클릭합니다

2. **IP 주소 제거**
   - 수신자 목록에서 제거할 IP를 선택합니다
   - "Remove" 버튼을 클릭합니다

3. **시작**
   - 최소 하나의 수신자 IP가 추가되어 있어야 합니다
   - "Start" 버튼을 클릭합니다
   - 상태 표시등이 보라색으로 변경되고 "Running"으로 표시됩니다

4. **중지**
   - "Stop" 버튼을 클릭합니다

5. **수신자 목록 저장**
   - "Save" 버튼을 클릭하여 현재 수신자 목록을 파일에 저장합니다
   - 다음 실행 시 자동으로 로드됩니다

## 주의사항

- **관리자 권한 필수**: Raw 소켓을 사용하기 위해 관리자 권한이 필요합니다
- Windows 방화벽 설정을 확인하세요
- 네트워크 어댑터가 활성화되어 있어야 합니다
- 실행 중에는 IP 주소 추가/제거가 비활성화됩니다

## 기술 스택

- **언어**: C# (.NET 5.0)
- **UI 프레임워크**: WPF
- **네트워크**: Raw Sockets (WinSock2)
- **개발 환경**: Visual Studio 2019
