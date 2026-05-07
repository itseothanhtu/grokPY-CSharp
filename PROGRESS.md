# 📊 PROGRESS.md — GrokPY Rewrite C# WPF
> **AI: ĐỌC FILE NÀY TRƯỚC TIÊN khi bắt đầu session mới**
> Cập nhật file này sau MỖI task hoàn thành.

---

## 🎯 Mục tiêu dự án
Viết lại toàn bộ **grokPY** (Python) → **C# WPF .NET 8**
- Repo gốc Python: `https://github.com/itseothanhtu/grokPY`
- Repo C# mới: `https://github.com/[YOUR_USERNAME]/grokPY-CSharp` ← cập nhật khi tạo xong

---

## 📍 TRẠNG THÁI HIỆN TẠI
```
PHASE: 5 — API Services
LAST_COMPLETED_TASK: Task 4.3 StatsigDiscovery.cs — Phase 4 hoàn thành
NEXT_TASK: PHASE_5 / Task 5.1 — GoogleImageService.cs (tạo ảnh Imagen 4)
```

---

## ✅ CHECKLIST TIẾN ĐỘ

### PHASE 1 — Setup dự án & cấu trúc
- [x] **1.1** Tạo GitHub repo `grokPY-CSharp`
- [x] **1.2** Tạo `.gitignore` cho C# / .NET
- [x] **1.3** Tạo Solution file `GrokPY.sln`
- [x] **1.4** Tạo project `GrokPY.App` (WPF .NET 8)
- [x] **1.5** Tạo project `GrokPY.Core` (Class Library)
- [x] **1.6** Tạo project `GrokPY.Services` (Class Library)
- [x] **1.7** Tạo `README.md` cơ bản
- [x] **1.8** Thêm NuGet packages cần thiết
- [x] **1.9** Push lên GitHub, verify CI/CD build pass

### PHASE 2 — Core Infrastructure
- [ ] **2.1** `Core/Models/AccountConfig.cs`
- [ ] **2.2** `Core/Models/VideoGenConfig.cs`
- [ ] **2.3** `Core/Models/AppSettings.cs`
- [ ] **2.4** `Core/Helpers/HmacHelper.cs` (license)
- [ ] **2.5** `Core/Helpers/MachineIdHelper.cs`
- [ ] **2.6** `Services/SettingsManager.cs`
- [ ] **2.7** `Services/LicenseManager.cs`

### PHASE 3 — Chrome / Browser Engine
- [ ] **3.1** `Services/Chrome/StealthChrome.cs` — launch Chrome, patch navigator.webdriver
- [ ] **3.2** `Services/Chrome/ChromeProcessManager.cs` — start/stop/find Chrome
- [ ] **3.3** `Services/Chrome/CdpSession.cs` — raw CDP WebSocket calls
- [ ] **3.4** `Services/Chrome/ChromeProfileManager.cs` — quản lý profiles

### PHASE 4 — Login & Auth
- [x] **4.1** `Services/Auth/LoginService.cs` — tự động login Google qua Chrome
- [x] **4.2** `Services/Auth/TokenExtractor.cs` — lấy access_token, cookie, sessionId, projectId
- [x] **4.3** `Services/Auth/StatsigDiscovery.cs` — lấy x-statsig-id cho Grok

### PHASE 5 — API Services
- [ ] **5.1** `Services/Api/GoogleImageService.cs` — tạo ảnh (Imagen 4)
- [ ] **5.2** `Services/Api/GoogleImageToImageService.cs` — image to image
- [ ] **5.3** `Services/Api/VeoTextToVideoService.cs` — text→video (Google Veo 3.1)
- [ ] **5.4** `Services/Api/VeoImageToVideoService.cs` — image→video (Google Veo 3.1)
- [ ] **5.5** `Services/Api/GrokTextToVideoService.cs` — text→video qua Grok
- [ ] **5.6** `Services/Api/GrokImageToVideoService.cs` — image→video qua Grok
- [ ] **5.7** `Services/Api/CharacterSyncService.cs` — lip sync / face sync
- [ ] **5.8** `Services/Api/SoraUploadService.cs` — upload ảnh lên Sora
- [ ] **5.9** `Services/Media/VideoMerger.cs` — ghép video

### PHASE 6 — Workflow Engine
- [ ] **6.1** `Services/Workflow/WorkflowRunner.cs` — chạy workflow nền
- [ ] **6.2** `Services/Workflow/IdeaToVideoWorkflow.cs` — idea→video pipeline
- [ ] **6.3** `Services/Workflow/WorkflowControl.cs` — start/stop/pause

### PHASE 7 — WPF UI
- [ ] **7.1** `App/MainWindow.xaml` + `MainWindow.xaml.cs`
- [ ] **7.2** `App/Views/Tabs/TabTextToVideo.xaml`
- [ ] **7.3** `App/Views/Tabs/TabImageToVideo.xaml`
- [ ] **7.4** `App/Views/Tabs/TabCreateImage.xaml`
- [ ] **7.5** `App/Views/Tabs/TabIdeaToVideo.xaml`
- [ ] **7.6** `App/Views/Tabs/TabCharacterSync.xaml`
- [ ] **7.7** `App/Views/Tabs/TabSettings.xaml`
- [ ] **7.8** `App/Views/Tabs/TabGrokSettings.xaml`
- [ ] **7.9** `App/Controls/StatusPanel.xaml`
- [ ] **7.10** `App/Controls/ProgressBar.xaml`
- [ ] **7.11** `App/Styles/Theme.xaml` — dark theme

### PHASE 8 — License UI
- [ ] **8.1** `App/Views/LicenseWindow.xaml` — cửa sổ nhập license
- [ ] **8.2** `App/Views/LicenseWindow.xaml.cs`

### PHASE 9 — Build & Packaging
- [ ] **9.1** Cấu hình `publish` single-file .exe
- [ ] **9.2** Viết `build.bat` script
- [ ] **9.3** Viết GitHub Actions CI/CD `.yml`
- [ ] **9.4** Test build trên Windows

---

## 📁 CẤU TRÚC THƯ MỤC MỤC TIÊU

```
GrokPY-CSharp/
├── GrokPY.sln
├── PROGRESS.md                          ← file này
├── README.md
├── .gitignore
├── build.bat
├── .github/
│   └── workflows/
│       └── build.yml
│
├── GrokPY.Core/                         ← Class Library
│   ├── GrokPY.Core.csproj
│   ├── Models/
│   │   ├── AccountConfig.cs
│   │   ├── VideoGenConfig.cs
│   │   └── AppSettings.cs
│   └── Helpers/
│       ├── HmacHelper.cs
│       └── MachineIdHelper.cs
│
├── GrokPY.Services/                     ← Class Library
│   ├── GrokPY.Services.csproj
│   ├── SettingsManager.cs
│   ├── LicenseManager.cs
│   ├── Chrome/
│   │   ├── StealthChrome.cs
│   │   ├── ChromeProcessManager.cs
│   │   ├── CdpSession.cs
│   │   └── ChromeProfileManager.cs
│   ├── Auth/
│   │   ├── LoginService.cs
│   │   ├── TokenExtractor.cs
│   │   └── StatsigDiscovery.cs
│   ├── Api/
│   │   ├── GoogleImageService.cs
│   │   ├── GoogleImageToImageService.cs
│   │   ├── VeoTextToVideoService.cs
│   │   ├── VeoImageToVideoService.cs
│   │   ├── GrokTextToVideoService.cs
│   │   ├── GrokImageToVideoService.cs
│   │   ├── CharacterSyncService.cs
│   │   └── SoraUploadService.cs
│   ├── Media/
│   │   └── VideoMerger.cs
│   └── Workflow/
│       ├── WorkflowRunner.cs
│       ├── IdeaToVideoWorkflow.cs
│       └── WorkflowControl.cs
│
└── GrokPY.App/                          ← WPF Application
    ├── GrokPY.App.csproj
    ├── App.xaml
    ├── App.xaml.cs
    ├── MainWindow.xaml
    ├── MainWindow.xaml.cs
    ├── Views/
    │   ├── Tabs/
    │   │   ├── TabTextToVideo.xaml(.cs)
    │   │   ├── TabImageToVideo.xaml(.cs)
    │   │   ├── TabCreateImage.xaml(.cs)
    │   │   ├── TabIdeaToVideo.xaml(.cs)
    │   │   ├── TabCharacterSync.xaml(.cs)
    │   │   ├── TabSettings.xaml(.cs)
    │   │   └── TabGrokSettings.xaml(.cs)
    │   └── LicenseWindow.xaml(.cs)
    ├── Controls/
    │   ├── StatusPanel.xaml(.cs)
    │   └── LogViewer.xaml(.cs)
    ├── ViewModels/
    │   ├── MainViewModel.cs
    │   ├── TextToVideoViewModel.cs
    │   ├── ImageToVideoViewModel.cs
    │   ├── CreateImageViewModel.cs
    │   └── SettingsViewModel.cs
    └── Styles/
        └── Theme.xaml
```

---

## 🔧 TECH STACK

| Thành phần | Package | Version |
|---|---|---|
| Framework | .NET | 8.0 |
| UI | WPF | built-in |
| Browser | PuppeteerSharp | 24.x |
| JSON | System.Text.Json | built-in |
| HTTP | HttpClient | built-in |
| Logging | Serilog | 3.x |
| MVVM | CommunityToolkit.Mvvm | 8.x |

---

## 📝 GHI CHÚ KỸ THUẬT QUAN TRỌNG

### StealthChrome — cách hoạt động
```
1. Launch Chrome.exe thật với --disable-blink-features=AutomationControlled
2. Kết nối PuppeteerSharp qua CDP (không dùng WebDriver)
3. Inject JS patch navigator.webdriver = undefined
4. Dùng page.EvaluateAsync() để gọi fetch() trong context browser
   → Cookie/session tự động đính kèm vì đang chạy trong Chrome thật
```

### Các endpoint quan trọng cần implement
```
Grok video:
  POST https://grok.com/rest/media/post/create
  POST https://grok.com/rest/app-chat/conversations/new
  POST https://grok.com/rest/media/video/upscale

Google AI (cần access_token từ Chrome session):
  POST https://aisandbox-pa.googleapis.com/v1/video:batchAsyncGenerateVideoText
  POST https://aisandbox-pa.googleapis.com/v1/video:batchCheckAsyncVideoGenerationStatus
  POST https://aisandbox-pa.googleapis.com/v1/projects/{id}/flowMedia:batchGenerateImages

Google Login flow:
  https://labs.google/fx/vi/tools/flow
  → Tự động click "Tạo bằng Flow" → nhập email/pass → lấy token
```

### Lưu ý bảo mật
```
- KHÔNG lưu password plain text (khác Python gốc)
- Dùng Windows DPAPI (ProtectedData) để mã hóa credentials
- File config: %APPDATA%\GrokPY\config.json (encrypted)
```

---

## 🗓️ LOG THAY ĐỔI
```
[2026-05-07] Session 1: Phase 1+2+3 hoàn thành — Build succeeded
[2026-05-07] Session 2: Task 4.1 LoginService + 4.2 TokenExtractor hoàn thành
[2026-05-07] Session 3: Task 4.3 StatsigDiscovery — Phase 4 hoàn thành
```

---

## 💬 HƯỚNG DẪN CHO AI

### Khi bắt đầu session mới, AI phải:
1. Đọc `PROGRESS.md` (file này)
2. Xác định `NEXT_TASK` hiện tại
3. Hỏi user confirm rồi bắt đầu viết code
4. Sau khi hoàn thành task, cập nhật:
   - Đánh dấu `[x]` vào checkbox tương ứng
   - Cập nhật `TRẠNG THÁI HIỆN TẠI`
   - Thêm dòng vào `LOG THAY ĐỔI`
5. Commit message format: `[PHASE_X] Task X.X — Mô tả ngắn`

### Quy tắc viết code:
- Mỗi file C# đầy đủ, không viết tắt
- Comment tiếng Việt để dễ đọc
- Async/await toàn bộ
- Không dùng blocking call (.Result, .Wait())
- Log đầy đủ qua ILogger
- Xử lý exception đầy đủ, không để crash
