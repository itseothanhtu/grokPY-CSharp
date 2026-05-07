# 🗺️ ROADMAP — GrokPY Rewrite C# WPF
> Lộ trình chi tiết từng bước, từ tạo repo đến build exe hoàn chỉnh

---

## BƯỚC 0 — Chuẩn bị (làm 1 lần duy nhất, do bạn tự làm)

### 0.1 Cài đặt môi trường
```
✅ Visual Studio 2022 (hoặc VS Code + C# extension)
✅ .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
✅ Git + GitHub account
✅ Google Chrome đã cài (tool sẽ dùng Chrome này)
```

### 0.2 Tạo GitHub repo
```
1. Vào https://github.com/new
2. Repository name: grokPY-CSharp
3. Description: GrokPY rewritten in C# WPF .NET 8
4. Public hoặc Private (tùy bạn)
5. Tick "Add README"
6. .gitignore: chọn "Visual Studio"
7. Nhấn "Create repository"
8. Clone về máy:
   git clone https://github.com/YOUR_USERNAME/grokPY-CSharp.git
   cd grokPY-CSharp
```

### 0.3 Cập nhật PROGRESS.md
```
Sau khi tạo repo, cập nhật dòng này trong PROGRESS.md:
  Repo C# mới: https://github.com/YOUR_USERNAME/grokPY-CSharp
```

---

## BƯỚC 1 — Tạo Solution & Projects

### 1.1 Tạo Solution + 3 Projects
```bash
# Chạy trong thư mục repo vừa clone
dotnet new sln -n GrokPY
dotnet new classlib -n GrokPY.Core -f net8.0
dotnet new classlib -n GrokPY.Services -f net8.0
dotnet new wpf -n GrokPY.App -f net8.0-windows

# Thêm vào solution
dotnet sln GrokPY.sln add GrokPY.Core/GrokPY.Core.csproj
dotnet sln GrokPY.sln add GrokPY.Services/GrokPY.Services.csproj
dotnet sln GrokPY.sln add GrokPY.App/GrokPY.App.csproj

# Thêm references
dotnet add GrokPY.Services/GrokPY.Services.csproj reference GrokPY.Core/GrokPY.Core.csproj
dotnet add GrokPY.App/GrokPY.App.csproj reference GrokPY.Core/GrokPY.Core.csproj
dotnet add GrokPY.App/GrokPY.App.csproj reference GrokPY.Services/GrokPY.Services.csproj
```

### 1.2 Thêm NuGet packages
```bash
# PuppeteerSharp - browser automation
dotnet add GrokPY.Services/GrokPY.Services.csproj package PuppeteerSharp

# Serilog - logging
dotnet add GrokPY.Services/GrokPY.Services.csproj package Serilog
dotnet add GrokPY.Services/GrokPY.Services.csproj package Serilog.Sinks.File
dotnet add GrokPY.Services/GrokPY.Services.csproj package Serilog.Sinks.Console
dotnet add GrokPY.App/GrokPY.App.csproj package Serilog

# MVVM
dotnet add GrokPY.App/GrokPY.App.csproj package CommunityToolkit.Mvvm

# System.Text.Json (nếu cần explicit)
dotnet add GrokPY.Core/GrokPY.Core.csproj package System.Text.Json
```

### 1.3 Tạo cấu trúc thư mục
```bash
# GrokPY.Core
mkdir GrokPY.Core/Models
mkdir GrokPY.Core/Helpers

# GrokPY.Services
mkdir GrokPY.Services/Chrome
mkdir GrokPY.Services/Auth
mkdir GrokPY.Services/Api
mkdir GrokPY.Services/Media
mkdir GrokPY.Services/Workflow

# GrokPY.App
mkdir GrokPY.App/Views
mkdir GrokPY.App/Views/Tabs
mkdir GrokPY.App/Controls
mkdir GrokPY.App/ViewModels
mkdir GrokPY.App/Styles
```

### 1.4 Tạo .github/workflows/build.yml
```yaml
name: Build GrokPY

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    runs-on: windows-latest
    steps:
    - uses: actions/checkout@v4
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '8.0.x'
    - name: Restore
      run: dotnet restore GrokPY.sln
    - name: Build
      run: dotnet build GrokPY.sln --configuration Release --no-restore
```

### 1.5 First commit
```bash
git add .
git commit -m "[PHASE_1] Initial solution structure"
git push origin main
```

---

## BƯỚC 2 — Core Models & Helpers

AI sẽ viết các file sau (theo thứ tự):

```
GrokPY.Core/Models/AccountConfig.cs
GrokPY.Core/Models/VideoGenConfig.cs  
GrokPY.Core/Models/AppSettings.cs
GrokPY.Core/Helpers/HmacHelper.cs
GrokPY.Core/Helpers/MachineIdHelper.cs
GrokPY.Services/SettingsManager.cs
GrokPY.Services/LicenseManager.cs
```

**Commit:** `[PHASE_2] Core models and helpers`

---

## BƯỚC 3 — StealthChrome Engine

AI sẽ viết (quan trọng nhất):

```
GrokPY.Services/Chrome/StealthChrome.cs
GrokPY.Services/Chrome/ChromeProcessManager.cs
GrokPY.Services/Chrome/CdpSession.cs
GrokPY.Services/Chrome/ChromeProfileManager.cs
```

**Key logic StealthChrome:**
```csharp
// 1. Tìm chrome.exe
// 2. Launch với --disable-blink-features=AutomationControlled
// 3. Kết nối PuppeteerSharp qua CDP (không WebDriver)
// 4. Patch navigator.webdriver = undefined
// 5. Expose method: EvaluateAsync() để inject JS
// 6. Expose method: GetCookiesAsync()
```

**Commit:** `[PHASE_3] StealthChrome browser engine`

---

## BƯỚC 4 — Login & Token

```
GrokPY.Services/Auth/LoginService.cs
GrokPY.Services/Auth/TokenExtractor.cs
GrokPY.Services/Auth/StatsigDiscovery.cs
```

**Key logic:**
```
LoginService:
  1. Mở Chrome → navigate https://labs.google/fx/vi/tools/flow
  2. Click "Tạo bằng Flow" → Google login page
  3. Nhập email, click Next
  4. Nhập password, click Next
  5. Chờ redirect về labs.google
  
TokenExtractor:
  1. Intercept request để lấy access_token từ _next/data
  2. Lấy sessionId từ submitBatchLog request
  3. Lấy projectId từ createProject response
  4. Lấy cookie từ browser context
  5. Lưu vào SettingsManager (mã hóa DPAPI)

StatsigDiscovery:
  1. Navigate grok.com/imagine
  2. Intercept request → lấy x-statsig-id header
  3. Cache vào file
```

**Commit:** `[PHASE_4] Login and token extraction`

---

## BƯỚC 5 — API Services

Viết theo thứ tự ưu tiên:

**5A — Google Image (đơn giản nhất, không cần browser):**
```
GrokPY.Services/Api/GoogleImageService.cs
```

**5B — Google Veo Video:**
```
GrokPY.Services/Api/VeoTextToVideoService.cs
GrokPY.Services/Api/VeoImageToVideoService.cs
```

**5C — Grok Video (dùng StealthChrome inject JS):**
```
GrokPY.Services/Api/GrokTextToVideoService.cs
GrokPY.Services/Api/GrokImageToVideoService.cs
```

**5D — Các service còn lại:**
```
GrokPY.Services/Api/GoogleImageToImageService.cs
GrokPY.Services/Api/CharacterSyncService.cs
GrokPY.Services/Api/SoraUploadService.cs
GrokPY.Services/Media/VideoMerger.cs
```

**Commit:** `[PHASE_5] All API services`

---

## BƯỚC 6 — Workflow Engine

```
GrokPY.Services/Workflow/WorkflowRunner.cs
GrokPY.Services/Workflow/IdeaToVideoWorkflow.cs
GrokPY.Services/Workflow/WorkflowControl.cs
```

**Commit:** `[PHASE_6] Workflow engine`

---

## BƯỚC 7 — WPF UI

Viết theo thứ tự:

**7A — Skeleton MainWindow:**
```
GrokPY.App/App.xaml
GrokPY.App/App.xaml.cs
GrokPY.App/MainWindow.xaml
GrokPY.App/MainWindow.xaml.cs
GrokPY.App/Styles/Theme.xaml  (dark theme)
```

**7B — Tabs (từng tab một):**
```
TabTextToVideo → TabImageToVideo → TabCreateImage
→ TabIdeaToVideo → TabCharacterSync → TabSettings → TabGrokSettings
```

**7C — Controls:**
```
StatusPanel.xaml — hiện log real-time
LogViewer.xaml — hiện progress bar
```

**7D — ViewModels (MVVM):**
```
MainViewModel.cs
TextToVideoViewModel.cs
...
```

**Commit:** `[PHASE_7] WPF UI complete`

---

## BƯỚC 8 — License Window

```
GrokPY.App/Views/LicenseWindow.xaml
GrokPY.App/Views/LicenseWindow.xaml.cs
```

**Commit:** `[PHASE_8] License window`

---

## BƯỚC 9 — Build & Release

**9.1 Cấu hình publish:**
```xml
<!-- GrokPY.App.csproj thêm: -->
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>true</SelfContained>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<PublishReadyToRun>true</PublishReadyToRun>
```

**9.2 build.bat:**
```bat
@echo off
dotnet publish GrokPY.App/GrokPY.App.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -o ./publish
echo Build xong! File: publish/GrokPY.App.exe
pause
```

**9.3 GitHub Actions release:**
```yaml
# Tự động build + upload .exe khi push tag v*
on:
  push:
    tags: ['v*']
```

**Commit:** `[PHASE_9] Build configuration and CI/CD`

---

## 📋 QUY ƯỚC COMMIT MESSAGE

```
[PHASE_1] Task X.X — Mô tả
[PHASE_2] Task X.X — Mô tả
...

Ví dụ:
[PHASE_3] Task 3.1 — StealthChrome launch và patch navigator
[PHASE_5] Task 5.3 — GrokTextToVideoService hoàn chỉnh
[FIX] Sửa lỗi TokenExtractor không lấy được cookie
[REFACTOR] SettingsManager dùng async
```

---

## ⚠️ NHỮNG ĐIỂM CẦN CHÚ Ý KHI VIẾT CODE

### 1. Password phải mã hóa
```csharp
// KHÔNG làm thế này (như Python gốc):
config["password"] = password; // plain text!

// PHẢI làm thế này:
var encrypted = ProtectedData.Protect(
    Encoding.UTF8.GetBytes(password),
    null,
    DataProtectionScope.CurrentUser
);
config.EncryptedPassword = Convert.ToBase64String(encrypted);
```

### 2. Async toàn bộ
```csharp
// KHÔNG:
var result = someTask.Result;

// PHẢI:
var result = await someTask;
```

### 3. Inject JS vào Chrome để gọi Grok API
```csharp
// Đây là cách gọi API Grok từ C# (qua Chrome session)
var result = await page.EvaluateAsync<string>(@"
    async () => {
        const res = await fetch('https://grok.com/rest/media/post/create', {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            credentials: 'include',  // Cookie tự đính kèm!
            body: JSON.stringify({ mediaType: 'MEDIA_POST_TYPE_VIDEO', prompt: '...' })
        });
        return await res.text();
    }
");
```

### 4. Google API dùng HttpClient thuần (không cần Chrome)
```csharp
// Sau khi có access_token từ LoginService
_httpClient.DefaultRequestHeaders.Authorization = 
    new AuthenticationHeaderValue("Bearer", accessToken);
var response = await _httpClient.PostAsync(url, content);
```
