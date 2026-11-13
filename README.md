# TOVI API

واجهة TOVI البرمجية: ASP.NET Core 8، و Entity Framework Core، و JWT، و Hangfire على SQL Server.

```bash
docker compose up -d
dotnet run --project src/Tovi.Api
```

أمر `docker compose` يتنفذ من جذر المستودع. للتجربة بدون SQL Server:

```bash
export Database__Provider=Sqlite
export ConnectionStrings__Tovi="Data Source=tovi.db"
dotnet run --project src/Tovi.Api
```

الخدمة على المنفذ `5088`. في التطوير، قاعدة فارغة تنشئ المدير `07700000001` / `ToviAdmin#1`.

```bash
dotnet test tests/Tovi.Tests/Tovi.Tests.csproj
```

الحل في `Tovi.sln`: `src/Tovi.Domain` و `src/Tovi.Api` و `tests/Tovi.Tests`.

## الحصص المباشرة والتقدّم

- غرفة الانتظار تفتح قبل البداية بـ 10 دقائق: المعلم يدخل ويجهّز، والطالب يشوف العد التنازلي وعدد الحاضرين، ويدخل البث من وقت البداية.
- `GET /sessions/{id}/live` حالة الحصة، و `POST /sessions/{id}/presence` نبضة كل 30 ثانية تحسب دقائق الحضور. الطالب يُحسب حاضر إذا حضر نص مدة الحصة.
- التفاعلات `POST /sessions/{id}/reactions`، والأسئلة مع التصويت `/sessions/{id}/questions`، والاستطلاعات `/sessions/{id}/polls`، وملخص المعلم `GET /sessions/{id}/summary`.
- التسجيل يشتغل فقط إذا `LiveKit:EgressEnabled` = `true` ويا خدمة LiveKit Egress. الملف ينحفظ بـ `LiveKit:EgressFilePrefix`، ورابط التشغيل يبدأ بـ `LiveKit:RecordingBaseUrl`. بدونها يرجع `recording_disabled`.
- نقاط الخبرة: 10 لإكمال درس، 20 لتسليم اختبار، 30 لحضور حصة، ومرة وحدة لكل نشاط. السلسلة أيام متتالية بتوقيت بغداد فيها نشاط. `GET /api/student/progress` يرجع النقاط والسلسلة والأوسمة.
- عند التشغيل، القاعدة القديمة تنضافلها الجداول والأعمدة الجديدة تلقائياً بدون حذف أي شي.
