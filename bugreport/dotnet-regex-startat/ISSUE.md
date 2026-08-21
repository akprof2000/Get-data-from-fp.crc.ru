# Regex.Match(input, startat) returns a match *before* startat with RegexOptions.Compiled

## Description

With `RegexOptions.Compiled`, `Regex.Match(input, startat)` can return a match whose `Index`
is **smaller** than the requested `startat`. This violates the documented contract and makes
every standard way of enumerating matches loop forever:

* `foreach (Match m in regex.Matches(input))` yields the *same* match indefinitely
* `Match.NextMatch()` returns the same match indefinitely
* a manual loop using `Match(input, pos)` never advances

The regex match itself is fast (single-digit milliseconds), so `matchTimeout` does not help —
the process appears to hang with no exception and no progress.

The same pattern and input behave differently depending on the engine:

| Options | Result |
| --- | --- |
| `IgnoreCase \| Singleline \| Compiled` | matches, and `Match(input, startat)` returns an earlier index (bug) |
| `IgnoreCase \| Singleline` (interpreted) | **no match at all** |
| `IgnoreCase \| Singleline \| NonBacktracking` | **no match at all** |

So besides the `startat` violation, the compiled engine and the interpreted/NonBacktracking
engines disagree on whether this pattern matches this input at all. At most one of them can
be right.

## Reproduction

Full self-contained repro is attached (`repro.csproj`, `Program.cs`, `input.txt`).

Pattern (Russian text — this came from a real document parser):

```csharp
const string Pattern =
    @"наименование\s+(?:ПРТО|РЭС)\s+и\s+место\s+расположения\s*(?:\(адрес\))?[:\s]*" +
    @"(?:[^\n]+\n){0,3}?([^\n]+(?:Республика|область)[^\n]*)";

var rx = new Regex(Pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
var first = rx.Match(text);                                   // index=251
var second = rx.Match(text, first.Index + first.Length);      // startat=255 -> index=251  (!!)
```

`input.txt` is 565 characters, 5 lines, LF line endings, UTF-8.

## Expected behavior

`Regex.Match(input, startat)` never returns a match starting before `startat`; therefore
`Matches()` enumeration and `NextMatch()` always terminate.

## Actual behavior

```
.NET 10.0.10, input length = 565

[Compiled] first match: index=251 length=4
[Compiled] Match(text, 255) -> index=251 length=4
[Compiled] *** BUG: returned a match at 251, before startat=255
[Compiled] *** foreach over Matches() never terminates (100000 iterations, same match)
[Compiled] *** NextMatch() never terminates either

[interpreted] no match at all

[NonBacktracking] no match at all
```

## Versions tested

Reproduces identically on:

* .NET 8.0.29
* .NET 9.0.18
* .NET 10.0.10

SDK 10.0.302, Windows 11 Pro 26200, x64. Not a regression — present in all three.

## Notes

Removing any of the following makes the first match disappear entirely (so the bug no longer
shows), which suggests the optional group combined with the lazy bounded repetition is involved:

* the optional group `(?:\(адрес\))?`
* the lazy bounded repetition `(?:[^\n]+\n){0,3}?`

Replacing the alternations `(?:ПРТО|РЭС)` / `(?:Республика|область)` with single literals does
**not** change the behavior — the bug still reproduces.

## Workaround

Search the remainder of the string instead of using the `startat` overload:

```csharp
int pos = 0;
while (pos <= text.Length)
{
    int offset = pos;
    var m = rx.Match(offset == 0 ? text : text[offset..]);   // not rx.Match(text, offset)
    if (!m.Success) break;
    pos = offset + m.Index + Math.Max(m.Length, 1);
    // report indices as offset + m.Index
}
```
