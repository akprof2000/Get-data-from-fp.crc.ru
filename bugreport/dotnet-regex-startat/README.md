# Материалы для issue в dotnet/runtime

Что куда:

| Файл | Назначение |
| --- | --- |
| `ISSUE.md` | Готовый текст issue (на английском) — вставить в форму на github.com/dotnet/runtime/issues/new |
| `Program.cs`, `repro.csproj`, `input.txt` | Самодостаточное воспроизведение, приложить архивом или вставить кодом |
| `original-document.txt` | Исходный документ конвейера, с которого всё началось (не обязателен для issue) |

Запуск воспроизведения:

    dotnet run -c Release -f net10.0
    dotnet run -c Release -f net9.0
    dotnet run -c Release -f net8.0

Суть: `Regex.Match(input, startat)` с `RegexOptions.Compiled` возвращает совпадение ДО
запрошенной позиции; из-за этого `Matches()`, `NextMatch()` и ручной обход зацикливаются.
Тот же шаблон в интерпретируемом движке и в `NonBacktracking` совпадений не находит вовсе.
Воспроизводится на .NET 8, 9 и 10 — не регрессия.

Обход, применённый в нашем коде (GetSiteData/ParseTextHeader/AddressParser.cs, TryPatterns):
искать в остатке строки `rx.Match(text[offset..])` вместо перегрузки со стартовой позицией.
