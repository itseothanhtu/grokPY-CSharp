# 🤖 AI_PROMPT.md
> Copy toàn bộ nội dung này và paste vào AI (Claude/GPT/Gemini) khi bắt đầu session mới

---

## PROMPT CHUẨN — DÁN VÀO AI KHI BẮT ĐẦU SESSION MỚI

```
Bạn là AI assistant giúp tôi viết dự án C# WPF. 

Trước khi làm bất cứ gì, hãy đọc 2 file sau trong repo GitHub của tôi:
1. PROGRESS.md — trạng thái tiến độ hiện tại
2. ROADMAP.md — lộ trình chi tiết

Repo: https://github.com/[YOUR_USERNAME]/grokPY-CSharp

Sau khi đọc xong:
1. Cho tôi biết NEXT_TASK là gì
2. Xác nhận bạn hiểu context
3. Hỏi tôi có muốn tiếp tục task đó không
4. Viết code đầy đủ cho task đó
5. Sau khi xong, cập nhật PROGRESS.md (đánh dấu [x], cập nhật NEXT_TASK, thêm log)

Quy tắc code:
- C# .NET 8, WPF
- Async/await toàn bộ
- Comment tiếng Việt
- Xử lý exception đầy đủ
- Mỗi file code đầy đủ, không bỏ qua phần nào
```

---

## PROMPT KHI MUỐN THÊM/SỬA TÍNH NĂNG

```
Tôi muốn [mô tả tính năng].

Context: Đây là dự án grokPY-CSharp, C# WPF .NET 8.
Đọc PROGRESS.md để biết đã làm đến đâu.
File liên quan: [tên file cần sửa]

Sau khi sửa, cập nhật PROGRESS.md nếu cần.
```

---

## PROMPT KHI GẶP LỖI

```
Tôi gặp lỗi sau khi chạy dự án grokPY-CSharp:

[DÁN ERROR MESSAGE VÀO ĐÂY]

File bị lỗi: [tên file]
Context: C# .NET 8, WPF, PuppeteerSharp

Hãy sửa lỗi và giải thích nguyên nhân.
Cập nhật PROGRESS.md nếu cần.
```

---

## HƯỚNG DẪN PUSH CODE LÊN GITHUB

### Cách 1: Bạn tự push (đơn giản nhất)
```
1. AI viết code → bạn copy vào file tương ứng trong VS/VS Code
2. git add .
3. git commit -m "[PHASE_X] Task X.X — Mô tả"
4. git push origin main
5. Cập nhật PROGRESS.md trên GitHub (edit trực tiếp hoặc push)
```

### Cách 2: Dùng GitHub CLI (nhanh hơn)
```bash
# Cài gh CLI: https://cli.github.com/
gh auth login

# Tạo file mới từ AI output
gh api repos/USERNAME/grokPY-CSharp/contents/path/to/file.cs \
  --method PUT \
  --field message="[PHASE_X] Task X.X" \
  --field content="$(base64 < file.cs)"
```

### Cách 3: GitHub Web Editor
```
1. Vào github.com/USERNAME/grokPY-CSharp
2. Navigate đến file cần sửa
3. Nhấn nút Edit (bút chì)
4. Paste code từ AI
5. Commit changes
```

---

## CHECKLIST TRƯỚC KHI BẮT ĐẦU SESSION

```
□ Đã đọc PROGRESS.md để biết đang ở đâu
□ Đã xác nhận NEXT_TASK
□ Chrome đã cài trên máy (dùng cho testing)
□ .NET 8 SDK đã cài
□ Repo đã clone về máy
```
