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
